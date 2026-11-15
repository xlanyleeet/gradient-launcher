use serde::Deserialize;
use serde_json::{Value, json};
use std::time::Duration;

const REPOSITORY: &str = "xlanyleeet/gradient-launcher";
const RELEASE_PREFIX: &str = "native-v";
const PAGE_SIZE: usize = 100;

#[derive(Deserialize)]
struct Release {
    tag_name: String,
    draft: bool,
    prerelease: bool,
    assets: Vec<Asset>,
}

#[derive(Deserialize)]
struct Asset {
    name: String,
    state: String,
    size: u64,
}

fn version(value: &str) -> Option<[u32; 3]> {
    let mut parts = value.split('.');
    let mut result = [0; 3];
    for number in &mut result {
        let part = parts.next()?;
        if part.is_empty()
            || part.len() > 4
            || (part.len() > 1 && part.starts_with('0'))
            || !part.bytes().all(|byte| byte.is_ascii_digit())
        {
            return None;
        }
        *number = part.parse().ok()?;
    }
    parts.next().is_none().then_some(result)
}

fn package() -> super::Result<&'static str> {
    match (std::env::consts::OS, std::env::consts::ARCH) {
        ("windows", "x86_64") => Ok("gradient-native-windows-x64.zip"),
        ("macos", "aarch64") => Ok("gradient-native-macos-arm64.tar.gz"),
        ("macos", "x86_64") => Ok("gradient-native-macos-x86_64.tar.gz"),
        _ => Err("Для цієї платформи немає релізного пакета Gradient.".into()),
    }
}

pub(super) async fn check() -> super::Result<Value> {
    let installed = option_env!("GRADIENT_NATIVE_VERSION")
        .unwrap_or(env!("CARGO_PKG_VERSION"));
    let installed_version =
        version(installed).ok_or("Невідома версія Gradient.")?;
    let package = package()?;
    let client = reqwest::Client::builder()
        .user_agent(format!("Gradient-Native/{installed}"))
        .timeout(Duration::from_secs(12))
        .build()?;
    let endpoint =
        format!("https://api.github.com/repos/{REPOSITORY}/releases");
    let mut latest = None;
    let mut page = 1;
    loop {
        let releases: Vec<Release> = client
            .get(&endpoint)
            .query(&[("per_page", PAGE_SIZE), ("page", page)])
            .header(reqwest::header::ACCEPT, "application/vnd.github+json")
            .send()
            .await?
            .error_for_status()?
            .json()
            .await?;
        let last = releases.len() < PAGE_SIZE;
        for entry in releases {
            if entry.draft
                || entry.prerelease
                || !entry.assets.iter().any(|asset| {
                    asset.name == package
                        && asset.state == "uploaded"
                        && asset.size > 0
                })
            {
                continue;
            }
            let Some(number) = entry
                .tag_name
                .strip_prefix(RELEASE_PREFIX)
                .and_then(version)
            else {
                continue;
            };
            if number > installed_version
                && latest.as_ref().is_none_or(|(old, _)| number > *old)
            {
                latest = Some((number, entry.tag_name));
            }
        }
        if last {
            break;
        }
        page += 1;
    }

    let available = latest.map(|(_, tag)| json!({
        "version": tag.trim_start_matches(RELEASE_PREFIX),
        "download_url": format!("https://github.com/{REPOSITORY}/releases/download/{tag}/{package}"),
    }));
    Ok(json!({ "installed": installed, "available": available }))
}
