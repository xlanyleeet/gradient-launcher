#![allow(dead_code, unused_imports, unused_variables)]
use std::collections::HashSet;
use std::time::Instant;
use tauri::plugin::TauriPlugin;
use tauri::{Manager, Runtime};
use tokio::sync::RwLock;

pub const OCCLUDED_AREA_THRESHOLD: f64 = 0.5;

pub struct AdsState {
    pub shown: bool,
    pub visibility_holds: usize,
    pub consent_required: bool,
    pub consent_notification_enabled: bool,
    pub consent_overlay_shown: bool,
    pub occluded: bool,
    pub last_click: Option<Instant>,
    pub malicious_origins: HashSet<String>,
}

impl Default for AdsState {
    fn default() -> Self {
        Self {
            shown: false,
            visibility_holds: 0,
            consent_required: false,
            consent_notification_enabled: false,
            consent_overlay_shown: false,
            occluded: false,
            last_click: None,
            malicious_origins: HashSet::new(),
        }
    }
}

pub fn init<R: Runtime>() -> TauriPlugin<R> {
    tauri::plugin::Builder::new("ads")
        .setup(|app, _api| {
            app.manage(RwLock::new(AdsState::default()));
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            init_ads_window,
            hide_ads_window,
            update_ads_window_hold,
            show_ads_consent_ui,
            expand_ads_consent_webview,
            open_ads_consent_preferences,
            finish_ads_consent_flow,
            should_show_ads_consent_popup,
            perform_ads_consent_action,
            record_ads_click,
            open_link,
            get_ads_personalization,
        ])
        .build()
}

#[tauri::command]
pub async fn init_ads_window<R: Runtime>(
    _app: tauri::AppHandle<R>,
    _dpr: f32,
    _override_shown: bool,
) -> crate::api::Result<()> {
    Ok(())
}

#[tauri::command]
pub async fn update_ads_window_hold<R: Runtime>(
    _app: tauri::AppHandle<R>,
    _acquire: bool,
    _dpr: f32,
) -> crate::api::Result<()> {
    Ok(())
}

#[tauri::command]
pub async fn hide_ads_window<R: Runtime>(
    _app: tauri::AppHandle<R>,
    _reset: Option<bool>,
) -> crate::api::Result<()> {
    Ok(())
}

#[tauri::command]
pub async fn show_ads_consent_ui<R: Runtime>(
    _app: tauri::AppHandle<R>,
    _notification_enabled: bool,
) -> crate::api::Result<()> {
    Ok(())
}

#[tauri::command]
pub async fn expand_ads_consent_webview<R: Runtime>(
    _app: tauri::AppHandle<R>,
) -> crate::api::Result<()> {
    Ok(())
}

#[tauri::command]
pub async fn open_ads_consent_preferences<R: Runtime>(
    _app: tauri::AppHandle<R>,
) -> crate::api::Result<()> {
    Ok(())
}

#[tauri::command]
pub async fn finish_ads_consent_flow<R: Runtime>(
    _app: tauri::AppHandle<R>,
    _dpr: Option<f32>,
) -> crate::api::Result<()> {
    Ok(())
}

#[tauri::command]
pub async fn should_show_ads_consent_popup<R: Runtime>(
    _app: tauri::AppHandle<R>,
) -> crate::api::Result<bool> {
    Ok(false)
}

#[tauri::command]
pub async fn perform_ads_consent_action<R: Runtime>(
    _app: tauri::AppHandle<R>,
    _action: String,
) -> crate::api::Result<()> {
    Ok(())
}

#[tauri::command]
pub async fn record_ads_click<R: Runtime>(
    _app: tauri::AppHandle<R>,
) -> crate::api::Result<()> {
    Ok(())
}

#[tauri::command]
pub async fn open_link<R: Runtime>(
    _app: tauri::AppHandle<R>,
    _path: String,
    _origin: String,
) -> crate::api::Result<()> {
    Ok(())
}

#[tauri::command]
pub async fn get_ads_personalization() -> crate::api::Result<bool> {
    Ok(false)
}
