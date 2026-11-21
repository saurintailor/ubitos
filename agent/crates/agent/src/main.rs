//! `ubit-agent`: the guest-side daemon. Run by systemd as the `ubit-agent` user.

use std::path::PathBuf;
use std::sync::Arc;

use clap::Parser;
use tracing::{error, info};
use ubit_agent::{AgentState, report, serve_connection};
use ubit_protocol::AGENT_VSOCK_PORT;
use ubit_scoring::Catalog;

#[derive(Debug, Clone)]
enum Listen {
    /// vsock port on any CID (production)
    Vsock(u32),
    /// TCP address (development and tests only)
    Tcp(String),
}

impl std::str::FromStr for Listen {
    type Err = String;
    fn from_str(s: &str) -> Result<Self, Self::Err> {
        if let Some(port) = s.strip_prefix("vsock:") {
            return port
                .parse()
                .map(Listen::Vsock)
                .map_err(|e| format!("bad vsock port: {e}"));
        }
        if let Some(addr) = s.strip_prefix("tcp:") {
            return Ok(Listen::Tcp(addr.to_string()));
        }
        Err("expected vsock:PORT or tcp:HOST:PORT".into())
    }
}

#[derive(Debug, Parser)]
#[command(version, about = "ubit guest agent")]
struct Args {
    /// Where to listen: vsock:PORT, or tcp:HOST:PORT for development.
    #[arg(long, default_value_t = format!("vsock:{AGENT_VSOCK_PORT}"))]
    listen: String,
    /// Directory of catalog *.yaml files.
    #[arg(long, default_value = "/usr/share/ubit/catalog")]
    catalog: PathBuf,
    /// Directory where index.html is written.
    #[arg(long, default_value = "/var/lib/ubit/report")]
    report_dir: PathBuf,
}

#[tokio::main(flavor = "current_thread")]
async fn main() -> std::process::ExitCode {
    tracing_subscriber::fmt()
        .with_env_filter(
            tracing_subscriber::EnvFilter::try_from_default_env().unwrap_or_else(|_| "info".into()),
        )
        .without_time() // journald adds timestamps
        .with_ansi(std::io::IsTerminal::is_terminal(&std::io::stderr()))
        .with_writer(std::io::stderr)
        .init();

    match run(Args::parse()).await {
        Ok(()) => std::process::ExitCode::SUCCESS,
        Err(e) => {
            error!("{e}");
            std::process::ExitCode::FAILURE
        }
    }
}

async fn run(args: Args) -> Result<(), String> {
    let listen: Listen = args.listen.parse()?;
    let catalog = Catalog::load_dir(&args.catalog).map_err(|e| e.to_string())?;
    info!(entries = catalog.entries.len(), version = %catalog.version, "catalog loaded");

    if let Err(e) = report::write(&args.report_dir, &report::render_pending()).await {
        // Not fatal: scoring still works, the host still gets its result.
        tracing::warn!(error = %e, "could not write the pending report");
    }

    let state = Arc::new(AgentState::new(catalog, args.report_dir));

    match listen {
        Listen::Tcp(addr) => {
            let listener = tokio::net::TcpListener::bind(&addr)
                .await
                .map_err(|e| format!("bind {addr}: {e}"))?;
            ready(
                &listener
                    .local_addr()
                    .map(|a| format!("tcp:{a}"))
                    .unwrap_or(addr),
            );
            loop {
                tokio::select! {
                    accepted = listener.accept() => {
                        let (stream, peer) = accepted.map_err(|e| e.to_string())?;
                        spawn_session(stream, state.clone(), peer.to_string());
                    }
                    _ = shutdown_signal() => break,
                }
            }
        }
        Listen::Vsock(port) => serve_vsock(port, state).await?,
    }
    info!("stopping");
    Ok(())
}

#[cfg(target_os = "linux")]
async fn serve_vsock(port: u32, state: Arc<AgentState>) -> Result<(), String> {
    use tokio_vsock::{VMADDR_CID_ANY, VsockAddr, VsockListener};
    let listener = VsockListener::bind(VsockAddr::new(VMADDR_CID_ANY, port))
        .map_err(|e| format!("bind vsock:{port}: {e}"))?;
    ready(&format!("vsock:{port}"));
    loop {
        tokio::select! {
            accepted = listener.accept() => {
                let (stream, peer) = accepted.map_err(|e| e.to_string())?;
                spawn_session(stream, state.clone(), format!("vsock:{}:{}", peer.cid(), peer.port()));
            }
            _ = shutdown_signal() => return Ok(()),
        }
    }
}

#[cfg(not(target_os = "linux"))]
async fn serve_vsock(_port: u32, _state: Arc<AgentState>) -> Result<(), String> {
    Err("vsock is only available on Linux; use --listen tcp:127.0.0.1:7710".into())
}

fn spawn_session<S>(stream: S, state: Arc<AgentState>, peer: String)
where
    S: tokio::io::AsyncRead + tokio::io::AsyncWrite + Unpin + Send + 'static,
{
    tokio::spawn(async move {
        if let Err(e) = serve_connection(stream, state, peer.clone()).await {
            info!(%peer, error = %e, "session ended");
        }
    });
}

/// Logs the line the image smoke test waits for, then tells systemd we are up.
fn ready(addr: &str) {
    info!("ubit-agent: listening on {addr}");
    #[cfg(target_os = "linux")]
    let _ = sd_notify::notify(&[sd_notify::NotifyState::Ready]);
}

async fn shutdown_signal() {
    #[cfg(unix)]
    {
        let mut term = tokio::signal::unix::signal(tokio::signal::unix::SignalKind::terminate())
            .expect("SIGTERM handler");
        tokio::select! { _ = term.recv() => {}, _ = tokio::signal::ctrl_c() => {} }
    }
    #[cfg(not(unix))]
    let _ = tokio::signal::ctrl_c().await;
}
