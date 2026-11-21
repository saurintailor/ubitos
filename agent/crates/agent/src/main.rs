//! `ubit-agent`: the guest-side daemon. Run by systemd as the `ubit-agent` user.
//!
//! Transports (any number, via repeated `--listen`):
//!   vsock:PORT     Virtualization.framework and Hyper-V hosts
//!   serial:PATH    a virtio-serial port, for QEMU hosts without vsock (Windows Home)
//!   tcp:HOST:PORT  development and tests only

use std::path::PathBuf;
use std::sync::Arc;
use std::time::Duration;

use clap::Parser;
use tracing::{error, info, warn};
use ubit_agent::{AgentState, report, serve_connection};
use ubit_protocol::AGENT_VSOCK_PORT;
use ubit_scoring::Catalog;

#[derive(Debug, Clone)]
enum Listen {
    Vsock(u32),
    Serial(PathBuf),
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
        if let Some(path) = s.strip_prefix("serial:") {
            return Ok(Listen::Serial(PathBuf::from(path)));
        }
        if let Some(addr) = s.strip_prefix("tcp:") {
            return Ok(Listen::Tcp(addr.to_string()));
        }
        Err("expected vsock:PORT, serial:PATH or tcp:HOST:PORT".into())
    }
}

#[derive(Debug, Parser)]
#[command(version, about = "ubit guest agent")]
struct Args {
    /// Where to listen (repeatable): vsock:PORT, serial:PATH, or tcp:HOST:PORT for development.
    #[arg(long, default_values_t = [format!("vsock:{AGENT_VSOCK_PORT}")])]
    listen: Vec<String>,
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
    let listens: Vec<Listen> = args
        .listen
        .iter()
        .map(|s| s.parse())
        .collect::<Result<_, _>>()?;
    let catalog = Catalog::load_dir(&args.catalog).map_err(|e| e.to_string())?;
    info!(entries = catalog.entries.len(), version = %catalog.version, "catalog loaded");

    if let Err(e) = report::write(&args.report_dir, &report::render_pending()).await {
        // Not fatal: scoring still works, the host still gets its result.
        warn!(error = %e, "could not write the pending report");
    }

    let state = Arc::new(AgentState::new(catalog, args.report_dir));
    let mut tasks = tokio::task::JoinSet::new();
    for listen in listens {
        match listen {
            Listen::Tcp(addr) => {
                let listener = tokio::net::TcpListener::bind(&addr)
                    .await
                    .map_err(|e| format!("bind {addr}: {e}"))?;
                let shown = listener.local_addr().map(|a| a.to_string()).unwrap_or(addr);
                info!("ubit-agent: listening on tcp:{shown}");
                tasks.spawn(serve_tcp(listener, state.clone()));
            }
            Listen::Vsock(port) => {
                tasks.spawn(serve_vsock(bind_vsock(port)?, state.clone()));
                info!("ubit-agent: listening on vsock:{port}");
            }
            Listen::Serial(path) => {
                info!("ubit-agent: listening on serial:{}", path.display());
                tasks.spawn(serve_serial(path, state.clone()));
            }
        }
    }
    #[cfg(target_os = "linux")]
    let _ = sd_notify::notify(&[sd_notify::NotifyState::Ready]);

    tokio::select! {
        _ = shutdown_signal() => {}
        Some(ended) = tasks.join_next() => {
            if let Ok(Err(e)) = ended {
                return Err(e);
            }
        }
    }
    info!("stopping");
    Ok(())
}

async fn serve_tcp(
    listener: tokio::net::TcpListener,
    state: Arc<AgentState>,
) -> Result<(), String> {
    loop {
        let (stream, peer) = listener.accept().await.map_err(|e| e.to_string())?;
        spawn_session(stream, state.clone(), peer.to_string());
    }
}

#[cfg(target_os = "linux")]
type VsockListener = tokio_vsock::VsockListener;
#[cfg(not(target_os = "linux"))]
type VsockListener = ();

#[cfg(target_os = "linux")]
fn bind_vsock(port: u32) -> Result<VsockListener, String> {
    use tokio_vsock::{VMADDR_CID_ANY, VsockAddr};
    VsockListener::bind(VsockAddr::new(VMADDR_CID_ANY, port))
        .map_err(|e| format!("bind vsock:{port}: {e}"))
}

#[cfg(not(target_os = "linux"))]
fn bind_vsock(_port: u32) -> Result<VsockListener, String> {
    Err("vsock is only available on Linux; use --listen tcp:127.0.0.1:7710".into())
}

#[cfg(target_os = "linux")]
async fn serve_vsock(listener: VsockListener, state: Arc<AgentState>) -> Result<(), String> {
    loop {
        let (stream, peer) = listener.accept().await.map_err(|e| e.to_string())?;
        spawn_session(
            stream,
            state.clone(),
            format!("vsock:{}:{}", peer.cid(), peer.port()),
        );
    }
}

#[cfg(not(target_os = "linux"))]
async fn serve_vsock(_listener: VsockListener, _state: Arc<AgentState>) -> Result<(), String> {
    Ok(())
}

/// Serves a virtio-serial port, one host session at a time. The port is a single
/// character device, so there is no accept: open it, talk until the host goes away
/// (reads end), and open it again. A missing device is waited for, not an error,
/// because the same image also boots on hosts that have no such port.
async fn serve_serial(path: PathBuf, state: Arc<AgentState>) -> Result<(), String> {
    let peer = format!("serial:{}", path.display());
    let mut warned = false;
    loop {
        match tokio::fs::OpenOptions::new()
            .read(true)
            .write(true)
            .open(&path)
            .await
        {
            Ok(port) => {
                warned = false;
                if let Err(e) = serve_connection(port, state.clone(), peer.clone()).await {
                    info!(%peer, error = %e, "session ended");
                }
                // No host attached reads as end of stream; don't spin.
                tokio::time::sleep(Duration::from_secs(1)).await;
            }
            Err(e) => {
                if e.kind() != std::io::ErrorKind::NotFound && !warned {
                    warn!(%peer, error = %e, "cannot open the serial port; will keep trying");
                    warned = true;
                }
                tokio::time::sleep(Duration::from_secs(5)).await;
            }
        }
    }
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
