//! End-to-end: a real agent session over an in-memory duplex stream.

use std::path::PathBuf;
use std::sync::Arc;

use tokio::io::{AsyncRead, DuplexStream};
use ubit_agent::{AgentState, serve_connection};
use ubit_protocol::{
    App, Body, Envelope, ErrorCode, Hello, HostInventory, HostOs, Nonce, decode, read_frame,
    write_message,
};
use ubit_scoring::Catalog;

fn state(report_dir: PathBuf) -> Arc<AgentState> {
    let catalog_dir = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../../catalog");
    Arc::new(AgentState::new(
        Catalog::load_dir(&catalog_dir).unwrap(),
        report_dir,
    ))
}

fn start(report_dir: PathBuf) -> (DuplexStream, tokio::task::JoinHandle<()>) {
    let (host, agent) = tokio::io::duplex(64 * 1024);
    let st = state(report_dir);
    let task = tokio::spawn(async move {
        let _ = serve_connection(agent, st, "test".into()).await;
    });
    (host, task)
}

async fn recv<R: AsyncRead + Unpin>(r: &mut R) -> Body {
    decode(&read_frame(r).await.unwrap().expect("agent closed early"))
        .unwrap()
        .body
}

#[tokio::test]
async fn full_session_scores_and_writes_the_report() {
    let dir = tempfile::tempdir().unwrap();
    let (host, task) = start(dir.path().join("report"));
    let (mut r, mut w) = tokio::io::split(host);

    let hello = Hello {
        host_os: HostOs::Windows,
        shell_version: "test".into(),
    };
    write_message(&mut w, &Envelope::new(Body::Hello(hello)))
        .await
        .unwrap();
    assert!(matches!(recv(&mut r).await, Body::HelloAck(_)));

    let inv = HostInventory {
        apps: vec![
            App {
                id: "k".into(),
                name: "Google Chrome".into(),
                source: "uninstall-hklm".into(),
            },
            App {
                id: "k2".into(),
                name: "Totally Unknown Tool".into(),
                source: "uninstall-hklm".into(),
            },
        ],
        default_browser: Some("ChromeHTML".into()),
        default_mail: None,
    };
    write_message(&mut w, &Envelope::new(Body::HostInventory(inv)))
        .await
        .unwrap();
    match recv(&mut r).await {
        Body::ScoreResult(result) => {
            assert_eq!(result.score, Some(100));
            assert_eq!(result.items[0].catalog_id, "browser.chrome");
            assert_eq!(result.unknown_apps, ["Totally Unknown Tool"]);
        }
        other => panic!("expected score_result, got {other:?}"),
    }

    let html = std::fs::read_to_string(dir.path().join("report/index.html")).unwrap();
    assert!(html.contains("Google Chrome") && html.contains("Firefox"));
    assert!(html.contains("ChromeHTML"));

    write_message(&mut w, &Envelope::new(Body::Ping(Nonce { nonce: 9 })))
        .await
        .unwrap();
    assert_eq!(recv(&mut r).await, Body::Pong(Nonce { nonce: 9 }));

    drop(w);
    drop(r);
    task.await.unwrap();
}

#[tokio::test]
async fn inventory_before_hello_is_refused_but_session_survives() {
    let dir = tempfile::tempdir().unwrap();
    let (host, _task) = start(dir.path().to_path_buf());
    let (mut r, mut w) = tokio::io::split(host);

    write_message(
        &mut w,
        &Envelope::new(Body::HostInventory(HostInventory::default())),
    )
    .await
    .unwrap();
    match recv(&mut r).await {
        Body::Error(e) => assert_eq!(e.code, ErrorCode::UnexpectedMessage),
        other => panic!("expected error, got {other:?}"),
    }
    write_message(&mut w, &Envelope::new(Body::Ping(Nonce { nonce: 1 })))
        .await
        .unwrap();
    assert_eq!(recv(&mut r).await, Body::Pong(Nonce { nonce: 1 }));
}

#[tokio::test]
async fn garbage_is_reported_and_a_wrong_version_closes_the_session() {
    use tokio::io::AsyncWriteExt;
    let dir = tempfile::tempdir().unwrap();
    let (host, task) = start(dir.path().to_path_buf());
    let (mut r, mut w) = tokio::io::split(host);

    let junk = b"{not json";
    w.write_all(&(junk.len() as u32).to_be_bytes())
        .await
        .unwrap();
    w.write_all(junk).await.unwrap();
    match recv(&mut r).await {
        Body::Error(e) => assert_eq!(e.code, ErrorCode::BadMessage),
        other => panic!("expected error, got {other:?}"),
    }

    let v2 = br#"{"v":2,"type":"ping","nonce":1}"#;
    w.write_all(&(v2.len() as u32).to_be_bytes()).await.unwrap();
    w.write_all(v2).await.unwrap();
    match recv(&mut r).await {
        Body::Error(e) => assert_eq!(e.code, ErrorCode::UnsupportedVersion),
        other => panic!("expected error, got {other:?}"),
    }
    task.await.unwrap();
    assert!(
        read_frame(&mut r).await.unwrap().is_none(),
        "agent should have closed"
    );
}

#[tokio::test]
async fn a_second_hello_starts_the_session_over() {
    let dir = tempfile::tempdir().unwrap();
    let (host, _task) = start(dir.path().to_path_buf());
    let (mut r, mut w) = tokio::io::split(host);
    for os in [HostOs::Windows, HostOs::Macos] {
        let hello = Hello {
            host_os: os,
            shell_version: "test".into(),
        };
        write_message(&mut w, &Envelope::new(Body::Hello(hello)))
            .await
            .unwrap();
        assert!(matches!(recv(&mut r).await, Body::HelloAck(_)));
    }
    // The session now scores as macOS: a bundle id matches, a Windows display name would not.
    let inv = HostInventory {
        apps: vec![App {
            id: "com.google.Chrome".into(),
            name: "x".into(),
            source: "applications".into(),
        }],
        ..Default::default()
    };
    write_message(&mut w, &Envelope::new(Body::HostInventory(inv)))
        .await
        .unwrap();
    match recv(&mut r).await {
        Body::ScoreResult(result) => assert_eq!(result.score, Some(100)),
        other => panic!("expected score_result, got {other:?}"),
    }
}
