//! `ubit-hostsim`: plays the host side of the protocol, for development and CI.
//!
//!     ubit-hostsim --connect tcp:127.0.0.1:7710 --inventory inv.json
//!     ubit-hostsim --connect vsock:3:7710          # from the hypervisor host, CID 3
//!
//! The inventory file may be a full `host_inventory` message or just its fields.

use std::path::PathBuf;
use std::time::Duration;

use clap::Parser;
use tokio::io::{AsyncRead, AsyncWrite};
use ubit_protocol::{
    Body, Envelope, Hello, HostInventory, HostOs, decode, read_frame, write_message,
};

#[derive(Debug, Parser)]
#[command(about = "Simulates a ubit host app talking to the agent")]
struct Args {
    /// tcp:HOST:PORT or vsock:CID:PORT
    #[arg(long, default_value = "tcp:127.0.0.1:7710")]
    connect: String,
    /// JSON inventory to send (defaults to a small built-in sample).
    #[arg(long)]
    inventory: Option<PathBuf>,
    /// Host OS to claim in hello. Defaults to the "os" implied by the inventory file name, else linux.
    #[arg(long, value_parser = ["macos", "windows", "linux"])]
    host_os: Option<String>,
    /// Keep retrying the connection for this many seconds (useful while a VM boots).
    #[arg(long, default_value_t = 0)]
    wait: u64,
    /// Print the score result as JSON instead of a summary.
    #[arg(long)]
    json: bool,
}

#[tokio::main(flavor = "current_thread")]
async fn main() -> std::process::ExitCode {
    match run(Args::parse()).await {
        Ok(()) => std::process::ExitCode::SUCCESS,
        Err(e) => {
            eprintln!("ubit-hostsim: {e}");
            std::process::ExitCode::FAILURE
        }
    }
}

async fn run(args: Args) -> Result<(), String> {
    let (inventory, implied_os) = load_inventory(args.inventory.as_deref())?;
    let host_os = match args.host_os.as_deref().or(implied_os) {
        Some("macos") => HostOs::Macos,
        Some("windows") => HostOs::Windows,
        _ => HostOs::Linux,
    };

    let deadline = tokio::time::Instant::now() + Duration::from_secs(args.wait);
    loop {
        match connect_and_talk(&args.connect, host_os, &inventory, args.json).await {
            Ok(()) => return Ok(()),
            Err(e) if tokio::time::Instant::now() < deadline => {
                eprintln!("ubit-hostsim: {e}; retrying");
                tokio::time::sleep(Duration::from_secs(2)).await;
            }
            Err(e) => return Err(e),
        }
    }
}

async fn connect_and_talk(
    target: &str,
    os: HostOs,
    inv: &HostInventory,
    json: bool,
) -> Result<(), String> {
    if let Some(addr) = target.strip_prefix("tcp:") {
        let stream = tokio::net::TcpStream::connect(addr)
            .await
            .map_err(|e| format!("connect {addr}: {e}"))?;
        return talk(stream, os, inv, json).await;
    }
    if let Some(rest) = target.strip_prefix("vsock:") {
        return vsock(rest, os, inv, json).await;
    }
    Err("expected tcp:HOST:PORT or vsock:CID:PORT".into())
}

#[cfg(target_os = "linux")]
async fn vsock(rest: &str, os: HostOs, inv: &HostInventory, json: bool) -> Result<(), String> {
    let (cid, port) = rest.split_once(':').ok_or("expected vsock:CID:PORT")?;
    let addr = tokio_vsock::VsockAddr::new(
        cid.parse().map_err(|e| format!("cid: {e}"))?,
        port.parse().map_err(|e| format!("port: {e}"))?,
    );
    let stream = tokio_vsock::VsockStream::connect(addr)
        .await
        .map_err(|e| format!("connect vsock:{rest}: {e}"))?;
    talk(stream, os, inv, json).await
}

#[cfg(not(target_os = "linux"))]
async fn vsock(_: &str, _: HostOs, _: &HostInventory, _: bool) -> Result<(), String> {
    Err("vsock is only available on Linux".into())
}

async fn talk<S: AsyncRead + AsyncWrite + Unpin>(
    stream: S,
    os: HostOs,
    inv: &HostInventory,
    json: bool,
) -> Result<(), String> {
    let (mut r, mut w) = tokio::io::split(stream);
    let send = |body| Envelope::new(body);
    let hello = Hello {
        host_os: os,
        shell_version: format!("hostsim-{}", env!("CARGO_PKG_VERSION")),
    };
    write_message(&mut w, &send(Body::Hello(hello)))
        .await
        .map_err(|e| e.to_string())?;
    match next(&mut r).await? {
        Body::HelloAck(ack) => eprintln!("agent {} on {}", ack.agent_version, ack.distro_version),
        other => return Err(format!("expected hello_ack, got {other:?}")),
    }
    write_message(&mut w, &send(Body::HostInventory(inv.clone())))
        .await
        .map_err(|e| e.to_string())?;
    match next(&mut r).await? {
        Body::ScoreResult(result) if json => {
            println!(
                "{}",
                serde_json::to_string_pretty(&send(Body::ScoreResult(result))).unwrap_or_default()
            );
        }
        Body::ScoreResult(result) => {
            match result.score {
                Some(s) => println!("readiness score: {s}/100"),
                None => println!("readiness score: none (no catalog matches)"),
            }
            for item in &result.items {
                println!("  {:<12} {} -> {}", item.status.label(), item.from, item.to);
            }
            if !result.unknown_apps.is_empty() {
                println!("  unknown: {}", result.unknown_apps.join(", "));
            }
        }
        other => return Err(format!("expected score_result, got {other:?}")),
    }
    Ok(())
}

async fn next<R: AsyncRead + Unpin>(r: &mut R) -> Result<Body, String> {
    let frame = read_frame(r)
        .await
        .map_err(|e| e.to_string())?
        .ok_or("agent closed the connection")?;
    let env = decode(&frame).map_err(|e| e.to_string())?;
    match env.body {
        Body::Error(err) => Err(format!("agent error {:?}: {}", err.code, err.message)),
        body => Ok(body),
    }
}

fn load_inventory(
    path: Option<&std::path::Path>,
) -> Result<(HostInventory, Option<&'static str>), String> {
    let Some(path) = path else {
        let sample = r#"{"apps":[
            {"id":"com.google.Chrome","name":"Google Chrome","source":"dev"},
            {"id":"com.tinyspeck.slackmacgap","name":"Slack","source":"dev"},
            {"id":"com.adobe.Photoshop","name":"Adobe Photoshop","source":"dev"}]}"#;
        return Ok((serde_json::from_str(sample).expect("built-in sample"), None));
    };
    let bytes = std::fs::read(path).map_err(|e| format!("{}: {e}", path.display()))?;
    let name = path
        .file_name()
        .unwrap_or_default()
        .to_string_lossy()
        .to_lowercase();
    let implied = if name.contains("windows") {
        Some("windows")
    } else if name.contains("mac") {
        Some("macos")
    } else {
        None
    };
    if let Ok(env) = decode(&bytes) {
        if let Body::HostInventory(inv) = env.body {
            return Ok((inv, implied));
        }
        return Err(format!("{}: not a host_inventory message", path.display()));
    }
    serde_json::from_slice(&bytes)
        .map(|inv| (inv, implied))
        .map_err(|e| format!("{}: {e}", path.display()))
}
