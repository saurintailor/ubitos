//! Scores a host inventory against the app catalog in `catalog/*.yaml`.
//!
//! Rule (v0): every catalog entry the inventory matches earns `weight x factor`, where
//! the factor comes from its status (native 1.0, alternative 0.8, workaround 0.4,
//! blocker 0). The score is earned weight over matched weight, as a percentage.
//! Apps that match nothing are reported as unknown and do not count.

use std::collections::{BTreeMap, BTreeSet, HashSet};
use std::path::Path;

use serde::Deserialize;
use ubit_protocol::{
    App, Category, HostInventory, HostOs, MAX_UNKNOWN_APPS, ScoreItem, ScoreResult, Status,
};

#[derive(Debug, thiserror::Error)]
pub enum CatalogError {
    #[error("reading {path}: {source}")]
    Io {
        path: String,
        source: std::io::Error,
    },
    #[error("parsing {path}: {source}")]
    Parse {
        path: String,
        source: serde_norway::Error,
    },
    #[error("catalog entry {id}: {problem}")]
    Invalid { id: String, problem: String },
    #[error("no catalog files (*.yaml) in {0}")]
    Empty(String),
}

/// One file in `catalog/`.
#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct CatalogFile {
    version: String,
    entries: Vec<Entry>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Entry {
    pub id: String,
    pub category: Category,
    /// Human name of the host app family, e.g. "Microsoft 365 / Office".
    pub name: String,
    #[serde(rename = "match")]
    pub matchers: Matchers,
    pub replacement: String,
    pub status: Status,
    #[serde(default = "default_weight")]
    pub weight: u32,
    #[serde(default)]
    pub note: Option<String>,
}

fn default_weight() -> u32 {
    1
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Matchers {
    /// Case-insensitive prefixes of the Windows display name.
    #[serde(default)]
    pub windows: Vec<String>,
    /// Case-insensitive exact macOS bundle identifiers.
    #[serde(default)]
    pub macos: Vec<String>,
}

impl Entry {
    fn matches(&self, os: HostOs, app: &App) -> bool {
        let win = || {
            let name = app.name.to_lowercase();
            self.matchers
                .windows
                .iter()
                .any(|p| name.starts_with(&p.to_lowercase()))
        };
        let mac = || {
            self.matchers
                .macos
                .iter()
                .any(|id| id.eq_ignore_ascii_case(&app.id))
        };
        match os {
            HostOs::Windows => win(),
            HostOs::Macos => mac(),
            HostOs::Linux => win() || mac(),
        }
    }
}

#[derive(Debug, Clone)]
pub struct Catalog {
    /// e.g. `apps@2026.11.1`, one `name@version` per file, comma separated.
    pub version: String,
    pub entries: Vec<Entry>,
}

impl Catalog {
    /// Loads every `*.yaml` file in `dir`, in file-name order.
    pub fn load_dir(dir: &Path) -> Result<Self, CatalogError> {
        let io = |source| CatalogError::Io {
            path: dir.display().to_string(),
            source,
        };
        let mut files: Vec<_> = std::fs::read_dir(dir)
            .map_err(io)?
            .filter_map(Result::ok)
            .map(|e| e.path())
            .filter(|p| p.extension().is_some_and(|x| x == "yaml" || x == "yml"))
            .collect();
        files.sort();
        if files.is_empty() {
            return Err(CatalogError::Empty(dir.display().to_string()));
        }
        let mut sources = Vec::new();
        for path in &files {
            let text = std::fs::read_to_string(path).map_err(|source| CatalogError::Io {
                path: path.display().to_string(),
                source,
            })?;
            let stem = path
                .file_stem()
                .unwrap_or_default()
                .to_string_lossy()
                .into_owned();
            sources.push((stem, text));
        }
        Self::from_sources(sources.iter().map(|(n, t)| (n.as_str(), t.as_str())))
    }

    /// Builds a catalog from `(file stem, YAML text)` pairs.
    pub fn from_sources<'a>(
        sources: impl IntoIterator<Item = (&'a str, &'a str)>,
    ) -> Result<Self, CatalogError> {
        let mut versions = Vec::new();
        let mut entries = Vec::new();
        for (name, text) in sources {
            let file: CatalogFile =
                serde_norway::from_str(text).map_err(|source| CatalogError::Parse {
                    path: name.to_string(),
                    source,
                })?;
            versions.push(format!("{name}@{}", file.version));
            entries.extend(file.entries);
        }
        let catalog = Catalog {
            version: versions.join(","),
            entries,
        };
        catalog.check()?;
        Ok(catalog)
    }

    fn check(&self) -> Result<(), CatalogError> {
        let mut seen = HashSet::new();
        for e in &self.entries {
            let bad = |problem: &str| CatalogError::Invalid {
                id: e.id.clone(),
                problem: problem.into(),
            };
            if !seen.insert(e.id.as_str()) {
                return Err(bad("duplicate id"));
            }
            if e.weight == 0 || e.weight > 10 {
                return Err(bad("weight must be between 1 and 10"));
            }
            if e.matchers.windows.is_empty() && e.matchers.macos.is_empty() {
                return Err(bad("needs at least one windows or macos matcher"));
            }
            if e.matchers
                .windows
                .iter()
                .chain(&e.matchers.macos)
                .any(|m| m.trim().is_empty())
            {
                return Err(bad("empty matcher"));
            }
        }
        Ok(())
    }

    /// Scores an inventory. Each catalog entry counts at most once.
    pub fn score(&self, os: HostOs, inventory: &HostInventory) -> ScoreResult {
        // entry index -> name of the first host app that matched it
        let mut matched: BTreeMap<usize, &str> = BTreeMap::new();
        let mut unknown: BTreeSet<&str> = BTreeSet::new();

        for app in &inventory.apps {
            match self.entries.iter().position(|e| e.matches(os, app)) {
                Some(i) => {
                    matched.entry(i).or_insert(app.name.as_str());
                }
                None => {
                    unknown.insert(app.name.as_str());
                }
            }
        }

        let (mut earned, mut total) = (0.0, 0.0);
        let mut items: Vec<ScoreItem> = matched
            .iter()
            .map(|(&i, &from)| {
                let e = &self.entries[i];
                earned += f64::from(e.weight) * e.status.factor();
                total += f64::from(e.weight);
                ScoreItem {
                    catalog_id: e.id.clone(),
                    category: e.category,
                    from: from.to_string(),
                    to: e.replacement.clone(),
                    status: e.status,
                }
            })
            .collect();
        // Hardest problems first, then alphabetical.
        items.sort_by(|a, b| {
            a.status
                .cmp(&b.status)
                .then_with(|| a.from.to_lowercase().cmp(&b.from.to_lowercase()))
        });

        let score = (total > 0.0).then(|| (earned / total * 100.0).round().clamp(0.0, 100.0) as u8);

        ScoreResult {
            score,
            items,
            unknown_apps: unknown
                .into_iter()
                .take(MAX_UNKNOWN_APPS)
                .map(str::to_string)
                .collect(),
            catalog_version: self.version.clone(),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;

    const SMALL: &str = r#"
version: "t1"
entries:
  - id: browser.chrome
    category: apps
    name: Google Chrome
    match: { windows: ["Google Chrome"], macos: ["com.google.Chrome"] }
    replacement: Firefox
    status: native
    weight: 3
  - id: office.m365
    category: apps
    name: Office
    match: { windows: ["Microsoft 365", "Microsoft Office"], macos: ["com.microsoft.Word", "com.microsoft.Excel"] }
    replacement: LibreOffice
    status: alternative
    weight: 3
  - id: design.photoshop
    category: apps
    name: Photoshop
    match: { windows: ["Adobe Photoshop"], macos: ["com.adobe.Photoshop"] }
    replacement: GIMP
    status: workaround
    weight: 2
  - id: legacy.thing
    category: apps
    name: Legacy
    match: { windows: ["Legacy CAD"] }
    replacement: none
    status: blocker
    weight: 2
"#;

    fn catalog() -> Catalog {
        Catalog::from_sources([("apps", SMALL)]).unwrap()
    }

    fn app(id: &str, name: &str) -> App {
        App {
            id: id.into(),
            name: name.into(),
            source: "test".into(),
        }
    }

    fn inv(apps: Vec<App>) -> HostInventory {
        HostInventory {
            apps,
            ..Default::default()
        }
    }

    #[test]
    fn windows_matches_by_case_insensitive_name_prefix() {
        let r = catalog().score(
            HostOs::Windows,
            &inv(vec![
                app("k1", "google chrome"),
                app("k2", "Microsoft 365 Apps for enterprise - en-us"),
            ]),
        );
        let ids: Vec<_> = r.items.iter().map(|i| i.catalog_id.as_str()).collect();
        assert_eq!(
            ids,
            ["office.m365", "browser.chrome"],
            "alternative sorts before native"
        );
        // (3*1.0 + 3*0.8) / 6 = 90%
        assert_eq!(r.score, Some(90));
    }

    #[test]
    fn macos_matches_by_exact_bundle_id_only() {
        let r = catalog().score(
            HostOs::Macos,
            &inv(vec![
                app("com.google.chrome", "Chrome"),
                app("com.example.Google Chrome", "Google Chrome"),
            ]),
        );
        assert_eq!(r.items.len(), 1);
        assert_eq!(r.items[0].from, "Chrome");
        assert_eq!(r.unknown_apps, ["Google Chrome"]);
    }

    #[test]
    fn one_entry_counts_once_even_with_many_matching_apps() {
        let r = catalog().score(
            HostOs::Macos,
            &inv(vec![
                app("com.microsoft.Word", "Word"),
                app("com.microsoft.Excel", "Excel"),
            ]),
        );
        assert_eq!(r.items.len(), 1);
        assert_eq!(r.items[0].from, "Word", "first matching app names the item");
        assert_eq!(r.score, Some(80));
    }

    #[test]
    fn blockers_pull_the_score_down_and_sort_first() {
        let r = catalog().score(
            HostOs::Windows,
            &inv(vec![
                app("a", "Legacy CAD 9"),
                app("b", "Adobe Photoshop 2026"),
                app("c", "Google Chrome"),
            ]),
        );
        assert_eq!(r.items[0].status, Status::Blocker);
        assert_eq!(r.items[1].status, Status::Workaround);
        // (0*2 + 0.4*2 + 1.0*3) / 7 = 54.3%
        assert_eq!(r.score, Some(54));
    }

    #[test]
    fn nothing_matched_means_no_score() {
        let r = catalog().score(HostOs::Windows, &inv(vec![app("x", "Contoso Tool")]));
        assert_eq!(r.score, None);
        assert_eq!(r.unknown_apps, ["Contoso Tool"]);
        assert_eq!(catalog().score(HostOs::Windows, &inv(vec![])).score, None);
    }

    #[test]
    fn duplicate_ids_and_bad_weights_are_rejected() {
        let dup = format!(
            "{SMALL}\n  - id: browser.chrome\n    category: apps\n    name: x\n    match: {{ windows: [x] }}\n    replacement: y\n    status: native\n"
        );
        assert!(matches!(
            Catalog::from_sources([("apps", dup.as_str())]),
            Err(CatalogError::Invalid { .. })
        ));
        let zero = SMALL.replace("weight: 3", "weight: 0");
        assert!(Catalog::from_sources([("apps", zero.as_str())]).is_err());
        let typo = SMALL.replace("replacement: GIMP", "replacment: GIMP");
        assert!(matches!(
            Catalog::from_sources([("apps", typo.as_str())]),
            Err(CatalogError::Parse { .. })
        ));
    }

    #[test]
    fn repository_catalog_loads_and_scores_the_fixtures_consistently() {
        let root = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../..");
        let catalog = Catalog::load_dir(&root.join("catalog")).expect("catalog/ must be valid");
        assert!(catalog.version.starts_with("apps@"));
        assert!(catalog.entries.len() >= 20);

        let load = |name: &str| -> HostInventory {
            let bytes = std::fs::read(root.join("protocol/fixtures/valid").join(name)).unwrap();
            match ubit_protocol::decode(&bytes).unwrap().body {
                ubit_protocol::Body::HostInventory(i) => i,
                other => panic!("{name}: not an inventory: {other:?}"),
            }
        };
        let win = catalog.score(HostOs::Windows, &load("host_inventory-windows.json"));
        let mac = catalog.score(HostOs::Macos, &load("host_inventory-macos.json"));

        // The two fixtures describe the same person on two hosts: same apps, same score.
        let ids = |r: &ScoreResult| {
            r.items
                .iter()
                .map(|i| i.catalog_id.clone())
                .collect::<BTreeSet<_>>()
        };
        assert_eq!(ids(&win), ids(&mac));
        assert_eq!(win.score, mac.score);
        assert!(win.unknown_apps.iter().any(|a| a.starts_with("Contoso")));
    }
}
