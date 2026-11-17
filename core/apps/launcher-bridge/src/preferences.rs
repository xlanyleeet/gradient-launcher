use std::path::PathBuf;

use serde::Deserialize;
use serde_json::{Value, json};
use theseus::{cache, process, settings};

use crate::Result;

#[derive(Deserialize)]
#[serde(tag = "action", rename_all = "snake_case")]
pub enum Request {
    Read,
    Save { values: Values },
    Locations,
    MoveDirectory { path: PathBuf },
    StoreUsage,
    StoreCleanup,
    StoreLimit { bytes: u64 },
    StoreRepair,
    ClearCache,
    ExportDebug { path: PathBuf },
}

#[derive(Deserialize)]
pub struct Values {
    compact_instance_cards: bool,
    show_play_time: bool,
    show_jump_in: bool,
    hide_installed_modpacks: bool,
    show_worlds_tab_in_instances: bool,
    show_files_tab_in_instances: bool,
    show_screenshots_tab_in_instances: bool,
    show_skin_selector_in_sidebar: bool,
    hide_on_process_start: bool,
    refocus_on_game_close: bool,
    warn_on_unknown_modpacks: bool,
    always_show_copy_details: bool,
    max_concurrent_downloads: usize,
    max_concurrent_writes: usize,
    custom_env_vars: Vec<(String, String)>,
    hooks: Hooks,
}

#[derive(Deserialize)]
struct Hooks {
    pre_launch: Option<String>,
    wrapper: Option<String>,
    post_exit: Option<String>,
}

pub async fn handle(request: Request) -> Result<Value> {
    match request {
        Request::Read => {
            let s = settings::get().await?;
            Ok(json!({
                "compact_instance_cards": s.compact_instance_cards,
                "show_play_time": s.show_play_time,
                "show_jump_in": s.show_jump_in,
                "hide_installed_modpacks": s.hide_installed_modpacks,
                "show_worlds_tab_in_instances": s.show_worlds_tab_in_instances,
                "show_files_tab_in_instances": s.show_files_tab_in_instances,
                "show_screenshots_tab_in_instances": s.show_screenshots_tab_in_instances,
                "show_skin_selector_in_sidebar": s.show_skin_selector_in_sidebar,
                "hide_on_process_start": s.hide_on_process_start,
                "refocus_on_game_close": s.refocus_on_game_close,
                "warn_on_unknown_modpacks": s.warn_on_unknown_modpacks,
                "always_show_copy_details": s.always_show_copy_details,
                "max_concurrent_downloads": s.max_concurrent_downloads,
                "max_concurrent_writes": s.max_concurrent_writes,
                "custom_env_vars": s.custom_env_vars,
                "hooks": s.hooks,
            }))
        }
        Request::Save { values: v } => {
            if !(1..=128).contains(&v.max_concurrent_downloads)
                || !(1..=128).contains(&v.max_concurrent_writes)
                || v.custom_env_vars.iter().any(|(key, value)| {
                    key.is_empty()
                        || key.contains(['=', '\0'])
                        || value.contains('\0')
                })
            {
                return Err(
                    "Invalid resource limits or environment variables".into()
                );
            }
            let mut s = settings::get().await?;
            s.compact_instance_cards = v.compact_instance_cards;
            s.show_play_time = v.show_play_time;
            s.show_jump_in = v.show_jump_in;
            s.hide_installed_modpacks = v.hide_installed_modpacks;
            s.show_worlds_tab_in_instances = v.show_worlds_tab_in_instances;
            s.show_files_tab_in_instances = v.show_files_tab_in_instances;
            s.show_screenshots_tab_in_instances =
                v.show_screenshots_tab_in_instances;
            s.show_skin_selector_in_sidebar = v.show_skin_selector_in_sidebar;
            s.hide_on_process_start = v.hide_on_process_start;
            s.refocus_on_game_close = v.refocus_on_game_close;
            s.warn_on_unknown_modpacks = v.warn_on_unknown_modpacks;
            s.always_show_copy_details = v.always_show_copy_details;
            s.max_concurrent_downloads = v.max_concurrent_downloads;
            s.max_concurrent_writes = v.max_concurrent_writes;
            s.custom_env_vars = v.custom_env_vars;
            s.hooks = settings::Hooks {
                pre_launch: v
                    .hooks
                    .pre_launch
                    .filter(|value| !value.trim().is_empty()),
                wrapper: v
                    .hooks
                    .wrapper
                    .filter(|value| !value.trim().is_empty()),
                post_exit: v
                    .hooks
                    .post_exit
                    .filter(|value| !value.trim().is_empty()),
            };
            settings::set(s).await?;
            Ok(Value::Null)
        }
        Request::Locations => {
            let d = theseus::State::get().await?.directories.clone();
            let backups = theseus::prelude::app_db_backup_dir()?;
            std::fs::create_dir_all(&backups)?;
            Ok(json!({ "data": d.config_dir, "backups": backups }))
        }
        Request::MoveDirectory { path } => {
            let path = std::fs::canonicalize(path)?;
            if !path.is_dir() {
                return Err("Select an existing directory".into());
            }
            if !process::get_all().await?.is_empty()
                || theseus::install::list_jobs(false).await?.iter().any(|job| {
                    matches!(
                        job.status,
                        theseus::install::InstallJobStatus::Running
                            | theseus::install::InstallJobStatus::Queued
                    )
                })
            {
                return Err("Close Minecraft and finish or cancel queued installations first".into());
            }
            let d = theseus::State::get().await?.directories.clone();
            let source = std::fs::canonicalize(&d.config_dir)?;
            if path == source {
                return Ok(json!(false));
            }
            if path.starts_with(&source)
                || source.starts_with(&path)
                || path.read_dir()?.next().is_some()
            {
                return Err(
                    "Select an empty folder outside the current data folder"
                        .into(),
                );
            }
            let mut s = settings::get().await?;
            s.prev_custom_dir = Some(
                source
                    .into_os_string()
                    .into_string()
                    .map_err(|_| "Invalid source path")?,
            );
            s.custom_dir = Some(
                path.into_os_string()
                    .into_string()
                    .map_err(|_| "Invalid destination path")?,
            );
            settings::set(s).await?;
            Ok(json!(true))
        }
        Request::StoreUsage => Ok(json!(settings::store_usage().await?)),
        Request::StoreCleanup => Ok(json!(settings::store_cleanup().await?)),
        Request::StoreLimit { bytes } => {
            settings::store_set_cache_limit(bytes).await?;
            Ok(Value::Null)
        }
        Request::StoreRepair => {
            if !process::get_all().await?.is_empty() {
                return Err(
                    "Close Minecraft before repairing game files".into()
                );
            }
            Ok(json!(
                settings::store_verify_with_progress(true, &|_, _| {}).await?
            ))
        }
        Request::ClearCache => {
            let types: Vec<_> = serde_json::from_value(json!([
                "project",
                "project_v3",
                "version",
                "user",
                "team",
                "organization",
                "file",
                "loader_manifest",
                "minecraft_manifest",
                "categories",
                "report_types",
                "loaders",
                "search_results"
            ]))?;
            cache::purge_cache_types(&types).await?;
            Ok(Value::Null)
        }
        Request::ExportDebug { path } => Ok(json!(
            theseus::debug_info::export_logs(
                path,
                tokio_util::sync::CancellationToken::new(),
                |_| {},
            )
            .await?
        )),
    }
}
