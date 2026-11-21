//! ubit host <-> agent protocol, version 1.
//!
//! Wire format: a 4-byte big-endian length, then one UTF-8 JSON object of at most
//! [`MAX_FRAME_LEN`] bytes. Every object carries `v` (protocol version) and `type`.
//! The JSON Schema in `protocol/v1.schema.json` is the source of truth; the fixtures
//! next to it are exercised by this crate's tests.

use serde::{Deserialize, Serialize};
use tokio::io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt};

/// Protocol version spoken by this crate.
pub const PROTOCOL_VERSION: u32 = 1;
/// Largest accepted frame body, in bytes.
pub const MAX_FRAME_LEN: usize = 1024 * 1024;
/// vsock port the agent listens on inside the guest.
pub const AGENT_VSOCK_PORT: u32 = 7710;
/// Most apps a host may send in one inventory.
pub const MAX_APPS: usize = 5000;
/// Most unknown app names an agent reports back.
pub const MAX_UNKNOWN_APPS: usize = 200;

/// One protocol message: the version plus a typed body.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct Envelope {
    pub v: u32,
    #[serde(flatten)]
    pub body: Body,
}

impl Envelope {
    /// Wraps a body with the current protocol version.
    pub fn new(body: Body) -> Self {
        Self {
            v: PROTOCOL_VERSION,
            body,
        }
    }

    /// Checks the rules JSON parsing alone cannot express.
    pub fn validate(&self) -> Result<(), ProtocolError> {
        if self.v != PROTOCOL_VERSION {
            return Err(ProtocolError::UnsupportedVersion(self.v));
        }
        match &self.body {
            Body::HostInventory(inv) if inv.apps.len() > MAX_APPS => {
                Err(ProtocolError::Invalid(format!(
                    "inventory has {} apps; the limit is {MAX_APPS}",
                    inv.apps.len()
                )))
            }
            Body::ScoreResult(r) if r.score.is_some_and(|s| s > 100) => Err(
                ProtocolError::Invalid("score must be between 0 and 100".into()),
            ),
            Body::ScoreResult(r) if r.unknown_apps.len() > MAX_UNKNOWN_APPS => Err(
                ProtocolError::Invalid(format!("at most {MAX_UNKNOWN_APPS} unknown apps")),
            ),
            _ => Ok(()),
        }
    }
}

/// Message bodies, tagged by the JSON `type` field.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum Body {
    Hello(Hello),
    HelloAck(HelloAck),
    HostInventory(HostInventory),
    ScoreResult(ScoreResult),
    Ping(Nonce),
    Pong(Nonce),
    Error(ErrorBody),
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum HostOs {
    Macos,
    Windows,
    /// Development hosts such as `ubit-hostsim`.
    Linux,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct Hello {
    pub host_os: HostOs,
    pub shell_version: String,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct HelloAck {
    pub agent_version: String,
    pub distro_version: String,
}

/// An installed app as the host sees it. Identifiers only; never file contents.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct App {
    /// macOS: bundle identifier. Windows: Uninstall registry key name.
    pub id: String,
    pub name: String,
    /// Where the host found it, e.g. `applications` or `uninstall-hklm`.
    pub source: String,
}

#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct HostInventory {
    pub apps: Vec<App>,
    #[serde(default)]
    pub default_browser: Option<String>,
    #[serde(default)]
    pub default_mail: Option<String>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum Category {
    Apps,
    Services,
    Devices,
}

/// How well the Linux side covers a host app. Ordered from worst to best.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum Status {
    Blocker,
    Workaround,
    Alternative,
    Native,
}

impl Status {
    /// Share of an entry's weight this status earns.
    pub fn factor(self) -> f64 {
        match self {
            Status::Native => 1.0,
            Status::Alternative => 0.8,
            Status::Workaround => 0.4,
            Status::Blocker => 0.0,
        }
    }

    pub fn label(self) -> &'static str {
        match self {
            Status::Native => "Native",
            Status::Alternative => "Alternative",
            Status::Workaround => "Workaround",
            Status::Blocker => "Blocker",
        }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ScoreItem {
    pub catalog_id: String,
    pub category: Category,
    /// The host app that matched, as the host named it.
    pub from: String,
    /// The Linux replacement.
    pub to: String,
    pub status: Status,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ScoreResult {
    /// 0-100, or `None` when nothing in the inventory matched the catalog.
    pub score: Option<u8>,
    pub items: Vec<ScoreItem>,
    pub unknown_apps: Vec<String>,
    pub catalog_version: String,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub struct Nonce {
    pub nonce: u64,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ErrorCode {
    UnsupportedVersion,
    BadMessage,
    UnexpectedMessage,
    FrameTooLarge,
    Internal,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ErrorBody {
    pub code: ErrorCode,
    pub message: String,
}

#[derive(Debug, thiserror::Error)]
pub enum ProtocolError {
    #[error("frame of {0} bytes exceeds the {MAX_FRAME_LEN}-byte limit")]
    FrameTooLarge(usize),
    #[error("protocol version {0} is not supported (this side speaks {PROTOCOL_VERSION})")]
    UnsupportedVersion(u32),
    #[error("malformed message: {0}")]
    BadMessage(#[from] serde_json::Error),
    #[error("invalid message: {0}")]
    Invalid(String),
    #[error("connection closed mid-frame")]
    Truncated,
    #[error(transparent)]
    Io(#[from] std::io::Error),
}

impl ProtocolError {
    /// The error code to send back to the peer for this failure.
    pub fn code(&self) -> ErrorCode {
        match self {
            ProtocolError::FrameTooLarge(_) => ErrorCode::FrameTooLarge,
            ProtocolError::UnsupportedVersion(_) => ErrorCode::UnsupportedVersion,
            ProtocolError::BadMessage(_) | ProtocolError::Invalid(_) => ErrorCode::BadMessage,
            ProtocolError::Truncated | ProtocolError::Io(_) => ErrorCode::Internal,
        }
    }

    /// Whether the connection can carry on after reporting this error.
    pub fn is_recoverable(&self) -> bool {
        matches!(
            self,
            ProtocolError::BadMessage(_) | ProtocolError::Invalid(_)
        )
    }
}

/// Parses and validates one frame body.
pub fn decode(body: &[u8]) -> Result<Envelope, ProtocolError> {
    let envelope: Envelope = serde_json::from_slice(body)?;
    envelope.validate()?;
    Ok(envelope)
}

/// Serializes an envelope into a complete frame (length prefix + JSON).
pub fn encode(envelope: &Envelope) -> Result<Vec<u8>, ProtocolError> {
    let body = serde_json::to_vec(envelope)?;
    if body.len() > MAX_FRAME_LEN {
        return Err(ProtocolError::FrameTooLarge(body.len()));
    }
    let mut frame = Vec::with_capacity(4 + body.len());
    frame.extend_from_slice(&(body.len() as u32).to_be_bytes());
    frame.extend_from_slice(&body);
    Ok(frame)
}

/// Reads one frame body. Returns `Ok(None)` on a clean end of stream between frames.
pub async fn read_frame<R: AsyncRead + Unpin>(
    reader: &mut R,
) -> Result<Option<Vec<u8>>, ProtocolError> {
    let mut len_buf = [0u8; 4];
    let mut filled = 0;
    while filled < 4 {
        let n = reader.read(&mut len_buf[filled..]).await?;
        if n == 0 {
            return if filled == 0 {
                Ok(None)
            } else {
                Err(ProtocolError::Truncated)
            };
        }
        filled += n;
    }
    let len = u32::from_be_bytes(len_buf) as usize;
    if len > MAX_FRAME_LEN {
        return Err(ProtocolError::FrameTooLarge(len));
    }
    let mut body = vec![0u8; len];
    reader
        .read_exact(&mut body)
        .await
        .map_err(|e| match e.kind() {
            std::io::ErrorKind::UnexpectedEof => ProtocolError::Truncated,
            _ => ProtocolError::Io(e),
        })?;
    Ok(Some(body))
}

/// Writes one message as a frame and flushes.
pub async fn write_message<W: AsyncWrite + Unpin>(
    writer: &mut W,
    envelope: &Envelope,
) -> Result<(), ProtocolError> {
    writer.write_all(&encode(envelope)?).await?;
    writer.flush().await?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;

    fn fixtures(kind: &str) -> Vec<(String, Vec<u8>)> {
        let dir = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("../../../protocol/fixtures")
            .join(kind);
        let mut out: Vec<_> = std::fs::read_dir(&dir)
            .unwrap_or_else(|e| panic!("reading {}: {e}", dir.display()))
            .map(|e| e.unwrap().path())
            .filter(|p| p.extension().is_some_and(|x| x == "json"))
            .map(|p| {
                (
                    p.file_name().unwrap().to_string_lossy().into_owned(),
                    std::fs::read(&p).unwrap(),
                )
            })
            .collect();
        out.sort();
        assert!(!out.is_empty(), "no fixtures in {}", dir.display());
        out
    }

    #[test]
    fn valid_fixtures_decode_and_round_trip() {
        for (name, bytes) in fixtures("valid") {
            let env = decode(&bytes).unwrap_or_else(|e| panic!("{name}: {e}"));
            let again = decode(&serde_json::to_vec(&env).unwrap()).unwrap();
            assert_eq!(env, again, "{name} did not round-trip");
        }
    }

    #[test]
    fn invalid_fixtures_are_rejected() {
        for (name, bytes) in fixtures("invalid") {
            assert!(decode(&bytes).is_err(), "{name} was accepted");
        }
    }

    #[test]
    fn unknown_fields_are_ignored() {
        let env = decode(br#"{"v":1,"type":"ping","nonce":7,"later":"field"}"#).unwrap();
        assert_eq!(env.body, Body::Ping(Nonce { nonce: 7 }));
    }

    #[test]
    fn wrong_version_is_not_recoverable() {
        let err = decode(br#"{"v":9,"type":"ping","nonce":1}"#).unwrap_err();
        assert_eq!(err.code(), ErrorCode::UnsupportedVersion);
        assert!(!err.is_recoverable());
    }

    #[tokio::test]
    async fn frames_round_trip_over_a_stream() {
        let msg = Envelope::new(Body::Hello(Hello {
            host_os: HostOs::Windows,
            shell_version: "0.1.0".into(),
        }));
        let mut bytes = encode(&msg).unwrap();
        bytes.extend(encode(&Envelope::new(Body::Ping(Nonce { nonce: 3 }))).unwrap());
        let mut reader = bytes.as_slice();
        assert_eq!(
            decode(&read_frame(&mut reader).await.unwrap().unwrap()).unwrap(),
            msg
        );
        assert!(read_frame(&mut reader).await.unwrap().is_some());
        assert!(
            read_frame(&mut reader).await.unwrap().is_none(),
            "clean EOF expected"
        );
    }

    #[tokio::test]
    async fn oversized_and_truncated_frames_fail() {
        let big_len = ((MAX_FRAME_LEN + 1) as u32).to_be_bytes();
        let mut big = big_len.as_slice();
        assert!(matches!(
            read_frame(&mut big).await,
            Err(ProtocolError::FrameTooLarge(_))
        ));
        let mut short: &[u8] = &[0, 0, 0, 10, b'{'];
        assert!(matches!(
            read_frame(&mut short).await,
            Err(ProtocolError::Truncated)
        ));
    }
}
