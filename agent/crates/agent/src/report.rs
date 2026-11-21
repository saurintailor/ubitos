//! The readiness report: a static HTML page the guest opens at login.
//! Everything is rendered locally from the score result; the page loads nothing remote.

use std::fmt::Write as _;
use std::path::Path;
use std::time::SystemTime;

use ubit_protocol::{HostInventory, HostOs, ScoreResult, Status};

const STYLE: &str = r#"
:root{--bg:#f6f7f9;--card:#fff;--ink:#1d2330;--muted:#5b6473;--line:#e3e6eb;--accent:#1f6f8b;
--native:#2e7d4f;--alt:#3b6fb6;--work:#b7791f;--block:#c0392b}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--ink);
font:15px/1.5 "Noto Sans",system-ui,-apple-system,"Segoe UI",sans-serif}
main{max-width:860px;margin:0 auto;padding:40px 24px 64px}
h1{font-size:26px;margin:0 0 4px}h2{font-size:17px;margin:32px 0 12px}
.sub{color:var(--muted);margin:0 0 28px}
.card{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:24px}
.hero{display:flex;gap:28px;align-items:center;flex-wrap:wrap}
.ring{--p:0;width:132px;height:132px;border-radius:50%;flex:none;display:grid;place-items:center;
background:conic-gradient(var(--accent) calc(var(--p)*1%),var(--line) 0)}
.ring span{width:104px;height:104px;border-radius:50%;background:var(--card);display:grid;place-items:center;
font-size:34px;font-weight:700}
.ring small{display:block;font-size:12px;font-weight:500;color:var(--muted);margin-top:-14px}
.counts{display:flex;gap:10px;flex-wrap:wrap;margin-top:10px}
.pill{display:inline-block;padding:2px 10px;border-radius:999px;font-size:13px;font-weight:600;white-space:nowrap}
.native{background:#e6f4ec;color:var(--native)}.alternative{background:#e8f0fb;color:var(--alt)}
.workaround{background:#fbf1e1;color:var(--work)}.blocker{background:#fbe7e5;color:var(--block)}
table{width:100%;border-collapse:collapse}th,td{text-align:left;padding:10px 8px;border-bottom:1px solid var(--line);vertical-align:top}
th{font-size:12px;text-transform:uppercase;letter-spacing:.04em;color:var(--muted);font-weight:600}
tr:last-child td{border-bottom:0}td.arrow{color:var(--muted);width:24px}
details{margin-top:12px}summary{cursor:pointer;color:var(--accent);font-weight:600}
ul.unknown{columns:2;margin:12px 0 0;padding-left:18px;color:var(--muted)}
footer{margin-top:32px;font-size:13px;color:var(--muted)}
.facts{display:grid;grid-template-columns:auto 1fr;gap:4px 16px;margin:0;font-size:14px}
.facts dt{color:var(--muted)}.facts dd{margin:0}
@media (max-width:560px){ul.unknown{columns:1}.hero{gap:16px}}
"#;

fn esc(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    for c in s.chars() {
        match c {
            '&' => out.push_str("&amp;"),
            '<' => out.push_str("&lt;"),
            '>' => out.push_str("&gt;"),
            '"' => out.push_str("&quot;"),
            '\'' => out.push_str("&#39;"),
            _ => out.push(c),
        }
    }
    out
}

fn page(title: &str, extra_head: &str, body: &str) -> String {
    format!(
        "<!doctype html>\n<html lang=\"en\"><head><meta charset=\"utf-8\">\
<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">\
<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">\
{extra_head}<title>{}</title><style>{STYLE}</style></head><body><main>{body}</main></body></html>\n",
        esc(title)
    )
}

const PRIVACY_NOTE: &str =
    "This report was made on this computer. Your app list was not sent anywhere.";

/// Shown before the host has shared its app list. Refreshes itself every 5 seconds.
pub fn render_pending() -> String {
    page(
        "Your switch readiness",
        "<meta http-equiv=\"refresh\" content=\"5\">",
        &format!(
            "<h1>Your switch readiness</h1><p class=\"sub\">Waiting for your computer to share its app list…</p>\
<div class=\"card\"><p>When the ubit app on Windows or macOS connects, this page fills in by itself. \
It checks which of your apps have a Linux version or a good replacement.</p></div><footer>{PRIVACY_NOTE}</footer>"
        ),
    )
}

/// The finished report for one scored inventory.
pub fn render_result(
    result: &ScoreResult,
    os: HostOs,
    inventory: &HostInventory,
    at: SystemTime,
) -> String {
    let host = match os {
        HostOs::Windows => "Windows",
        HostOs::Macos => "macOS",
        HostOs::Linux => "a test host",
    };
    let count = |s: Status| result.items.iter().filter(|i| i.status == s).count();

    let mut body = String::new();
    let _ = write!(
        body,
        "<h1>Your switch readiness</h1><p class=\"sub\">Based on the apps installed on your {host} computer.</p>"
    );

    let (pct, headline) = match result.score {
        Some(s) => (s, format!("{s}")),
        None => (0, "–".to_string()),
    };
    let summary = match result.score {
        None => "None of your apps are in our catalog yet, so there is no score.".to_string(),
        Some(s) if s >= 85 => "Almost everything you use already works here.".to_string(),
        Some(s) if s >= 60 => {
            "Most of what you use works here; a few apps need a replacement.".to_string()
        }
        Some(_) => {
            "Several apps need a replacement or a workaround. The list below shows where to start."
                .to_string()
        }
    };
    let _ = write!(
        body,
        "<div class=\"card hero\"><div class=\"ring\" style=\"--p:{pct}\"><span>{headline}<small>of 100</small></span></div>\
<div><strong>{}</strong><div class=\"counts\">",
        esc(&summary)
    );
    for status in [
        Status::Native,
        Status::Alternative,
        Status::Workaround,
        Status::Blocker,
    ] {
        let n = count(status);
        if n > 0 {
            let _ = write!(
                body,
                "<span class=\"pill {}\">{n} {}</span>",
                status_class(status),
                count_label(status, n)
            );
        }
    }
    body.push_str("</div></div></div>");

    if !result.items.is_empty() {
        body.push_str("<h2>Your apps on Linux</h2><div class=\"card\"><table><thead><tr><th>You use</th><th></th><th>On Linux</th><th>Status</th></tr></thead><tbody>");
        for item in &result.items {
            let _ = write!(
                body,
                "<tr><td>{}</td><td class=\"arrow\">→</td><td>{}</td><td><span class=\"pill {}\">{}</span></td></tr>",
                esc(&item.from),
                esc(&item.to),
                status_class(item.status),
                item.status.label()
            );
        }
        body.push_str("</tbody></table></div>");
    }

    if !result.unknown_apps.is_empty() {
        let _ = write!(
            body,
            "<details><summary>{} we don't know yet</summary><ul class=\"unknown\">",
            match result.unknown_apps.len() {
                1 => "1 app".to_string(),
                n => format!("{n} apps"),
            }
        );
        for name in &result.unknown_apps {
            let _ = write!(body, "<li>{}</li>", esc(name));
        }
        body.push_str("</ul></details>");
    }

    body.push_str("<h2>Details</h2><div class=\"card\"><dl class=\"facts\">");
    let fact = |b: &mut String, k: &str, v: &str| {
        let _ = write!(b, "<dt>{}</dt><dd>{}</dd>", esc(k), esc(v));
    };
    fact(&mut body, "Apps checked", &inventory.apps.len().to_string());
    if let Some(b) = &inventory.default_browser {
        fact(&mut body, "Default browser", b);
    }
    if let Some(m) = &inventory.default_mail {
        fact(&mut body, "Default mail app", m);
    }
    fact(&mut body, "Catalog", &result.catalog_version);
    fact(
        &mut body,
        "Made at",
        &humantime::format_rfc3339_seconds(at).to_string(),
    );
    body.push_str("</dl></div>");

    let _ = write!(body, "<footer>{PRIVACY_NOTE}</footer>");
    page("Your switch readiness", "", &body)
}

/// "native", "1 alternative", "2 alternatives", ...
fn count_label(s: Status, n: usize) -> &'static str {
    match (s, n) {
        (Status::Native, _) => "native",
        (Status::Alternative, 1) => "alternative",
        (Status::Alternative, _) => "alternatives",
        (Status::Workaround, 1) => "workaround",
        (Status::Workaround, _) => "workarounds",
        (Status::Blocker, 1) => "blocker",
        (Status::Blocker, _) => "blockers",
    }
}

fn status_class(s: Status) -> &'static str {
    match s {
        Status::Native => "native",
        Status::Alternative => "alternative",
        Status::Workaround => "workaround",
        Status::Blocker => "blocker",
    }
}

/// Writes `index.html` atomically: a temp file unique to this write, then rename.
/// Concurrent sessions never share a temp file; the last rename wins.
pub async fn write(dir: &Path, html: &str) -> std::io::Result<()> {
    static NEXT: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
    tokio::fs::create_dir_all(dir).await?;
    let n = NEXT.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
    let tmp = dir.join(format!(".index.html.{}.{n}.tmp", std::process::id()));
    tokio::fs::write(&tmp, html).await?;
    tokio::fs::rename(&tmp, dir.join("index.html")).await
}

#[cfg(test)]
mod tests {
    use super::*;
    use ubit_protocol::{App, Category, ScoreItem};

    #[test]
    fn host_supplied_text_is_escaped() {
        let result = ScoreResult {
            score: Some(50),
            items: vec![ScoreItem {
                catalog_id: "x".into(),
                category: Category::Apps,
                from: "<script>alert(1)</script>".into(),
                to: "Safe & sound".into(),
                status: Status::Workaround,
            }],
            unknown_apps: vec!["\"quoted\"".into()],
            catalog_version: "apps@t".into(),
        };
        let inv = HostInventory {
            apps: vec![App {
                id: "a".into(),
                name: "b".into(),
                source: "c".into(),
            }],
            ..Default::default()
        };
        let html = render_result(&result, HostOs::Windows, &inv, SystemTime::UNIX_EPOCH);
        assert!(!html.contains("<script>"));
        assert!(html.contains("&lt;script&gt;"));
        assert!(html.contains("Safe &amp; sound"));
        assert!(html.contains("&quot;quoted&quot;"));
        assert!(html.contains("1970-01-01T00:00:00Z"));
    }

    #[tokio::test]
    async fn concurrent_writes_never_mix_and_leave_no_temp_files() {
        let dir = std::env::temp_dir().join(format!("ubit-report-{}", std::process::id()));
        let pages: Vec<String> = (0..32)
            .map(|i| format!("page-{i}-{}", "x".repeat(50_000)))
            .collect();
        let mut set = tokio::task::JoinSet::new();
        for page in pages.clone() {
            let dir = dir.clone();
            set.spawn(async move { write(&dir, &page).await });
        }
        while let Some(r) = set.join_next().await {
            r.unwrap().unwrap();
        }
        let final_page = std::fs::read_to_string(dir.join("index.html")).unwrap();
        assert!(pages.contains(&final_page), "report is a mix of writers");
        let leftovers = std::fs::read_dir(&dir)
            .unwrap()
            .filter(|e| e.as_ref().unwrap().file_name() != "index.html")
            .count();
        assert_eq!(leftovers, 0);
        std::fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn missing_score_renders_a_dash() {
        let result = ScoreResult {
            score: None,
            items: vec![],
            unknown_apps: vec![],
            catalog_version: "v".into(),
        };
        let html = render_result(
            &result,
            HostOs::Macos,
            &HostInventory::default(),
            SystemTime::UNIX_EPOCH,
        );
        assert!(html.contains("no score"));
        assert!(render_pending().contains("http-equiv=\"refresh\""));
    }
}
