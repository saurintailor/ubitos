//! The ubit guest agent: one session per host connection.
//!
//! Session rules: the host speaks first with `hello`; `host_inventory` is only accepted
//! after that; `ping` works any time. Recoverable errors are reported and the session
//! carries on; version mismatches and broken framing end it.

pub mod report;

use std::path::PathBuf;
use std::sync::Arc;

use tokio::io::{AsyncRead, AsyncWrite};
use tracing::{debug, info, warn};
use ubit_protocol::{
    Body, Envelope, ErrorBody, ErrorCode, HelloAck, HostOs, Nonce, ProtocolError, decode,
    read_frame, write_message,
};
use ubit_scoring::Catalog;

pub const AGENT_VERSION: &str = env!("CARGO_PKG_VERSION");

/// Shared, read-only state for all sessions.
pub struct AgentState {
    pub catalog: Catalog,
    pub report_dir: PathBuf,
    pub distro_version: String,
}

impl AgentState {
    pub fn new(catalog: Catalog, report_dir: PathBuf) -> Self {
        Self {
            catalog,
            report_dir,
            distro_version: distro_version(),
        }
    }
}

/// `PRETTY_NAME` from os-release, e.g. "Ubuntu 26.04 LTS".
pub fn distro_version() -> String {
    ["/etc/os-release", "/usr/lib/os-release"]
        .iter()
        .find_map(|p| std::fs::read_to_string(p).ok())
        .and_then(|text| {
            text.lines()
                .find_map(|l| l.strip_prefix("PRETTY_NAME="))
                .map(|v| v.trim_matches('"').to_string())
        })
        .unwrap_or_else(|| "unknown".into())
}

#[derive(Debug, thiserror::Error)]
pub enum SessionError {
    #[error(transparent)]
    Protocol(#[from] ProtocolError),
}

/// Serves one host connection until it closes or breaks the protocol.
pub async fn serve_connection<S>(
    stream: S,
    state: Arc<AgentState>,
    peer: String,
) -> Result<(), SessionError>
where
    S: AsyncRead + AsyncWrite + Unpin,
{
    let (mut reader, mut writer) = tokio::io::split(stream);
    let mut host_os: Option<HostOs> = None;
    // Logged on the first frame, not on open: a serial port with no host attached
    // reads as an immediate end of stream, and that is not a connection.
    let mut active = false;

    loop {
        let frame = match read_frame(&mut reader).await {
            Ok(Some(frame)) => frame,
            Ok(None) => break,
            Err(e) => {
                warn!(%peer, error = %e, "framing error; closing");
                let _ = send_error(&mut writer, e.code(), &e.to_string()).await;
                return Err(e.into());
            }
        };

        if !active {
            active = true;
            info!(%peer, "host connected");
        }

        let envelope = match decode(&frame) {
            Ok(env) => env,
            Err(e) => {
                warn!(%peer, error = %e, "rejected message");
                send_error(&mut writer, e.code(), &e.to_string()).await?;
                if e.is_recoverable() {
                    continue;
                }
                return Err(e.into());
            }
        };

        match (envelope.body, host_os) {
            // A repeated hello starts the session over. Serial ports have no disconnect
            // signal, so a host app that restarts simply says hello again.
            (Body::Hello(hello), _) => {
                info!(%peer, host_os = ?hello.host_os, shell = %hello.shell_version, "hello");
                host_os = Some(hello.host_os);
                let ack = HelloAck {
                    agent_version: AGENT_VERSION.into(),
                    distro_version: state.distro_version.clone(),
                };
                write_message(&mut writer, &Envelope::new(Body::HelloAck(ack))).await?;
            }
            (Body::HostInventory(inventory), Some(os)) => {
                let result = state.catalog.score(os, &inventory);
                info!(%peer, apps = inventory.apps.len(), score = ?result.score, "scored inventory");
                let page =
                    report::render_result(&result, os, &inventory, std::time::SystemTime::now());
                if let Err(e) = report::write(&state.report_dir, &page).await {
                    warn!(error = %e, dir = %state.report_dir.display(), "could not write report");
                }
                write_message(&mut writer, &Envelope::new(Body::ScoreResult(result))).await?;
            }
            (Body::HostInventory(_), None) => {
                send_error(
                    &mut writer,
                    ErrorCode::UnexpectedMessage,
                    "send hello first",
                )
                .await?;
            }
            (Body::Ping(Nonce { nonce }), _) => {
                write_message(&mut writer, &Envelope::new(Body::Pong(Nonce { nonce }))).await?;
            }
            (Body::Pong(_), _) => debug!(%peer, "pong"),
            (Body::Error(err), _) => {
                warn!(%peer, code = ?err.code, message = %err.message, "host reported an error")
            }
            (Body::HelloAck(_) | Body::ScoreResult(_), _) => {
                send_error(
                    &mut writer,
                    ErrorCode::UnexpectedMessage,
                    "that message only travels agent -> host",
                )
                .await?;
            }
        }
    }

    if active {
        info!(%peer, "host disconnected");
    }
    Ok(())
}

async fn send_error<W: AsyncWrite + Unpin>(
    writer: &mut W,
    code: ErrorCode,
    message: &str,
) -> Result<(), ProtocolError> {
    let message: String = message.chars().take(500).collect();
    write_message(
        writer,
        &Envelope::new(Body::Error(ErrorBody { code, message })),
    )
    .await
}
