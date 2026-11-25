use serde::Deserialize;
use serde_json::{Value, json};
use std::error::Error;
use std::fs::OpenOptions;
use std::io::{self, BufRead, Write};
use std::path::{Component, Path, PathBuf};
use std::time::{Duration, Instant};
use theseus::{
    EventState, State, install, instance, logs, minecraft_auth, process, worlds,
};

mod discover;
mod skin;
mod preferences;
mod updates;

type Result<T> = std::result::Result<T, Box<dyn Error>>;

#[derive(Deserialize)]
#[serde(tag = "method", rename_all = "snake_case")]
enum Request {
    Library,
    QuickPlay,
    CheckUpdate,
    PrepareLauncherUpdate,
    CreateProfileShortcut {
        id: String,
    },
    ResolveProfileShortcut {
        url: String,
    },
    UploadSkin {
        path: PathBuf,
        variant: SkinVariant,
    },
    CurrentSkinPreview,
    PreviewSkin { path: PathBuf, variant: SkinVariant },
    ResetSkin,
    JobDiagnostics {
        id: uuid::Uuid,
    },
    InstalledModVersions {
        id: String,
        path: String,
    },
    SwitchModVersion {
        id: String,
        path: String,
        version: String,
    },
    ProfileFolder {
        id: String,
    },
    Groups {
        id: String,
    },
    CreateGroup {
        name: String,
    },
    SetGroups {
        id: String,
        groups: Vec<String>,
    },
    ExportCandidates {
        id: String,
    },
    ExportProfile {
        id: String,
        path: PathBuf,
        included: Vec<String>,
    },
    ImportSources {
        launcher: theseus::pack::import::ImportLauncherType,
        base: PathBuf,
    },
    ImportProfile {
        launcher: theseus::pack::import::ImportLauncherType,
        base: PathBuf,
        folder: String,
    },
    UpdateContent {
        id: String,
        path: String,
    },
    UpdateAllContent {
        id: String,
    },
    ModpackVersions {
        id: String,
    },
    UpdateModpack {
        id: String,
        version: String,
    },
    Files {
        id: String,
        path: String,
    },
    PreviewFile {
        id: String,
        path: String,
    },
    CopyFile {
        id: String,
        path: String,
        destination: PathBuf,
    },
    SaveProfileText {
        id: String,
        path: String,
        before: String,
        text: String,
    },
    Discover {
        #[serde(flatten)]
        request: discover::Request,
    },
    LaunchSettings,
    JavaRuntimes,
    DetectJava {
        major: String,
    },
    SetJavaRuntime {
        major: String,
        path: PathBuf,
    },
    DownloadJavaRuntime {
        major: String,
    },
    ProfileLaunchSettings {
        id: String,
    },
    SaveProfileLaunchSettings {
        id: String,
        memory: String,
        java: String,
    },
    SaveLaunchSettings {
        values: LaunchSettings,
    },
    Preferences { request: preferences::Request },
    SetDiscordPresence {
        enabled: bool,
    },
    GameVersions {
        #[serde(default)]
        include_snapshots: bool,
    },
    LoaderVersions {
        loader: theseus::data::ModLoader,
        game_version: String,
    },
    CreateProfile {
        name: String,
        version: String,
        loader: theseus::data::ModLoader,
        #[serde(default)]
        loader_version: Option<String>,
    },
    RenameProfile {
        id: String,
        name: String,
    },
    ProfileIcon {
        id: String,
        path: PathBuf,
    },
    DuplicateProfile {
        id: String,
    },
    RemoveProfile {
        id: String,
    },
    Accounts,
    UseAccount {
        id: uuid::Uuid,
    },
    RemoveAccount {
        id: uuid::Uuid,
    },
    Cover {
        id: String,
    },
    Screenshots {
        id: String,
    },
    ExportScreenshots {
        id: String,
        path: PathBuf,
    },
    Play {
        id: String,
    },
    Stop {
        id: String,
    },
    Log {
        id: String,
    },
    LogHistory {
        id: String,
    },
    ReadLog {
        id: String,
        kind: logs::LogType,
        filename: String,
    },
    Content {
        id: String,
        #[serde(default)]
        include_pack: bool,
    },
    AddMod {
        id: String,
        path: PathBuf,
    },
    AddContent {
        id: String,
        path: PathBuf,
        kind: theseus::data::ProjectType,
    },
    SetContentEnabled {
        id: String,
        path: String,
        action: ContentAction,
    },
    RemoveContent {
        id: String,
        path: String,
    },
    Worlds {
        id: String,
    },
    Servers {
        id: String,
    },
    SaveServer {
        id: String,
        server: Option<String>,
        name: String,
        address: String,
    },
    RemoveServer {
        id: String,
        server: String,
    },
    JoinServer {
        id: String,
        server: String,
    },
    OpenWorld {
        id: String,
        world: String,
    },
    BackupWorld {
        id: String,
        world: String,
    },
    Queue,
    Import {
        path: PathBuf,
    },
    Job {
        id: uuid::Uuid,
        action: JobAction,
    },
    OfflineLogin {
        name: String,
    },
    BeginLogin,
    FinishLogin {
        code: String,
    },
    CancelLogin,
}

#[derive(Deserialize)]
#[serde(rename_all = "snake_case")]
enum SkinVariant {
    Classic,
    Slim,
}

#[derive(serde::Serialize, Deserialize)]
struct LaunchSettings {
    memory_mib: u32,
    fullscreen: bool,
    width: u16,
    height: u16,
    arguments: Vec<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    discord_rpc: Option<bool>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    system_memory_mib: Option<u64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    recommended_memory_mib: Option<u32>,
}

#[derive(Deserialize)]
#[serde(rename_all = "snake_case")]
enum JobAction {
    Pause,
    Resume,
    Cancel,
    Retry,
}

#[derive(Deserialize)]
#[serde(rename_all = "snake_case")]
enum ContentAction {
    Enable,
    Disable,
}

type PendingLogin = Option<(Instant, theseus::data::MinecraftLoginFlow)>;

async fn require_stopped(id: &str) -> Result<()> {
    if !process::get_by_instance_id(id).await?.is_empty() {
        return Err("Спочатку закрийте Minecraft для цього профілю.".into());
    }
    Ok(())
}

fn supported_java_major(value: &str) -> Result<u32> {
    let major = value.parse()?;
    if !matches!(major, 8 | 17 | 21 | 25) {
        return Err("Виберіть Java 8, 17, 21 або 25.".into());
    }
    Ok(major)
}

async fn find_server(
    id: &str,
    server: &str,
    write: bool,
) -> Result<(usize, String, worlds::ServerPackStatus)> {
    for world in worlds::get_instance_worlds(id).await? {
        if let worlds::WorldDetails::Server {
            index,
            server_id,
            source,
            address,
            pack_status,
            ..
        } = world.details
            && server_id == server
        {
            if write
                && !matches!(
                    source,
                    instance::ServerSource::UserSynced
                        | instance::ServerSource::LocalDesynced
                )
            {
                return Err("Сервером керує модпак або проєкт.".into());
            }
            return Ok((index, address, pack_status));
        }
    }
    Err("Сервер не знайдено.".into())
}

async fn local_world(id: &str, world: &str) -> Result<worlds::World> {
    if !matches!(
        Path::new(world).components().collect::<Vec<_>>().as_slice(),
        [Component::Normal(_)]
    ) {
        return Err("Неприпустима назва папки світу.".into());
    }
    Ok(worlds::get_singleplayer_world(id, world).await?)
}

async fn handle(request: Request, login: &mut PendingLogin) -> Result<Value> {
    Ok(match request {
        Request::CheckUpdate => updates::check().await?,
        Request::PrepareLauncherUpdate => {
            if !process::get_all().await?.is_empty() || install::list_jobs(false).await?.iter().any(|job| {
                matches!(job.status, install::InstallJobStatus::Queued | install::InstallJobStatus::Running)
            }) {
                return Err("Закрийте Minecraft і дочекайтеся завершення встановлень перед оновленням Gradient.".into());
            }
            Value::Null
        }
        Request::CreateProfileShortcut { id } => {
            let profile = instance::get(&id).await?.ok_or("Профіль не знайдено.")?;
            let mut url = url::Url::parse("gradient-native://launch/instance")?;
            url.query_pairs_mut().append_pair("id", &profile.instance.id);
            json!(url.as_str())
        }
        Request::ResolveProfileShortcut { url } => {
            let url = url::Url::parse(&url)?;
            if url.scheme() != "gradient-native" || url.host_str() != Some("launch")
                || url.path() != "/instance" || !url.username().is_empty()
                || url.password().is_some() || url.port().is_some() || url.fragment().is_some() {
                return Err("Непідтримуваний ярлик профілю.".into());
            }
            let mut fields = url.query_pairs();
            let Some((key, value)) = fields.next() else {
                return Err("Ярлик не містить профілю.".into());
            };
            if key != "id" || value.is_empty() || fields.next().is_some() {
                return Err("Непідтримуваний ярлик профілю.".into());
            }
            let profile = instance::get(&value).await?.ok_or("Профіль не знайдено.")?;
            json!({ "id": profile.instance.id, "name": profile.instance.name, "quarantined": profile.quarantined })
        }
        Request::UploadSkin { path, variant } => {
            let bytes = skin::read_texture(&path)?;
            let variant = match variant {
                SkinVariant::Classic => theseus::minecraft_skins::MinecraftSkinVariant::Classic,
                SkinVariant::Slim => theseus::minecraft_skins::MinecraftSkinVariant::Slim,
            };
            if let Some(id) = skin::active_offline_account().await? {
                skin::save_offline(id, &bytes, matches!(variant, theseus::minecraft_skins::MinecraftSkinVariant::Slim))?;
                return Ok(Value::Null);
            }
            let cape = Box::pin(theseus::minecraft_skins::get_available_capes())
                .await?.into_iter().find(|cape| cape.is_equipped);
            Box::pin(theseus::minecraft_skins::add_and_equip_custom_skin(bytes.into(), variant, cape)).await?;
            Box::pin(theseus::minecraft_skins::flush_pending_skin_change()).await?;
            Value::Null
        }
        Request::CurrentSkinPreview => Box::pin(skin::current_preview()).await?,
        Request::PreviewSkin { path, variant } =>
            skin::preview(&skin::read_texture(&path)?, matches!(variant, SkinVariant::Slim))?,
        Request::ResetSkin => {
            if let Some(id) = skin::active_offline_account().await? {
                skin::reset_offline(id)?;
                return Ok(Value::Null);
            }
            Box::pin(theseus::minecraft_skins::unequip_skin()).await?;
            Box::pin(theseus::minecraft_skins::flush_pending_skin_change()).await?;
            Value::Null
        }
        Request::JobDiagnostics { id } => json!(install::job_support_details(id).await?),
        Request::InstalledModVersions { id, path } => discover::installed_mod_versions(&id, &path).await?,
        Request::SwitchModVersion { id, path, version } => {
            discover::switch_installed_mod(&id, &path, &version).await?;
            Value::Null
        }
        Request::ProfileFolder { id } => json!(instance::get_full_path(&id).await?),
        Request::Groups { id } => {
            let profile = instance::get(&id).await?.ok_or("Профіль не знайдено.")?;
            json!(instance::list_groups().await?.into_iter().map(|group| {
                json!({ "id": group.id, "name": group.name, "selected": profile.group_ids.contains(&group.id) })
            }).collect::<Vec<_>>())
        }
        Request::CreateGroup { name } => json!(instance::create_group(name).await?),
        Request::SetGroups { id, groups } => {
            instance::set_group_memberships(vec![instance::InstanceGroupMembershipUpdate { instance_id: id, group_ids: groups }]).await?;
            Value::Null
        }
        Request::ExportCandidates { id } => json!(instance::get_pack_export_candidates(&id).await?),
        Request::ExportProfile { id, path, included } => {
            require_stopped(&id).await?;
            instance::export_mrpack(&id, path, included, Vec::new(), None, None, None).await?;
            Value::Null
        }
        Request::ImportSources { launcher, base } => {
            let mut folders = theseus::pack::import::get_importable_instances(launcher, base).await?;
            folders.sort_by_cached_key(|folder| folder.to_lowercase());
            json!(folders)
        }
        Request::ImportProfile { launcher, base, folder } => {
            let folders = theseus::pack::import::get_importable_instances(launcher, base.clone()).await?;
            if !folders.contains(&folder) { return Err("Профіль для імпорту не знайдено.".into()); }
            install::import_instance(launcher, base, folder).await?;
            Value::Null
        }
        Request::UpdateContent { id, path } => {
            require_stopped(&id).await?;
            instance::update_project(&id, &path, None).await?;
            Value::Null
        }
        Request::UpdateAllContent { id } => {
            require_stopped(&id).await?;
            let items = instance::get_content_items(&id, None).await?;
            let mut updated = 0u32;
            let mut failed = 0u32;
            for item in items {
                if item.has_update
                    && !item.locked
                    && item.synced_pack.is_none()
                    && !item.file_path.is_empty()
                {
                    match instance::update_project(&id, &item.file_path, None).await {
                        Ok(_) => updated += 1,
                        Err(_) => failed += 1,
                    }
                }
            }
            json!({ "updated": updated, "failed": failed })
        }
        Request::ModpackVersions { id } => {
            let profile = instance::get(&id).await?.ok_or("Профіль не знайдено.")?;
            let theseus::data::InstanceLink::ModrinthModpack {
                project_id,
                version_id,
            } = profile.link
            else {
                return Err("Профіль не пов’язаний із модпаком Modrinth.".into());
            };
            let mut versions = Box::pin(theseus::cache::get_project_versions(
                &project_id,
                Some(theseus::data::CacheBehaviour::MustRevalidate),
            ))
            .await?
            .unwrap_or_default();
            versions.sort_by_key(|v| std::cmp::Reverse(v.date_published));
            let options: Vec<_> = versions
                .into_iter()
                .map(|v| {
                    json!({
                        "id": v.id,
                        "name": v.version_number,
                        "channel": v.version_type,
                        "game_versions": v.game_versions,
                    })
                })
                .collect();
            json!({ "current_id": version_id, "options": options })
        }
        Request::UpdateModpack { id, version } => {
            require_stopped(&id).await?;
            Box::pin(instance::update_managed_modrinth_version(&id, &version))
                .await?;
            Value::Null
        }
        Request::Files { id, path } => {
            let mut entries = instance::list_instance_files(&id, &path).await?;
            entries.sort_by_cached_key(|entry| (entry.kind != "directory", entry.name.to_lowercase()));
            json!(entries)
        }
        Request::PreviewFile { id, path } => json!(instance::preview_instance_text(&id, &path).await?),
        Request::CopyFile { id, path, destination } => {
            instance::save_instance_file_as(&id, &path, &destination).await?;
            Value::Null
        }
        Request::SaveProfileText { id, path, before, text } => {
            require_stopped(&id).await?;
            if text.len() > 256 * 1024 || text.contains('\0') {
                return Err("Редактор підтримує текстові файли до 256 КіБ.".into());
            }
            if instance::preview_instance_text(&id, &path).await? != before {
                return Err("Файл змінився на диску. Відкрийте його знову перед збереженням.".into());
            }
            instance::write_instance_file(&id, &path, text.as_bytes(), false).await?;
            Value::Null
        }
        Request::Discover { request } => Box::pin(discover::handle(request)).await?,
        Request::LaunchSettings => {
            let settings = theseus::settings::get().await?;
            let system_memory_kib = theseus::jre::get_max_memory().await.unwrap_or(0);
            json!(LaunchSettings {
                memory_mib: settings.memory.maximum,
                fullscreen: settings.force_fullscreen,
                width: settings.game_resolution.0,
                height: settings.game_resolution.1,
                arguments: settings.extra_launch_args,
                discord_rpc: Some(settings.discord_rpc),
                system_memory_mib: (system_memory_kib > 0).then_some(system_memory_kib / 1024),
                recommended_memory_mib: Some(theseus::jre::default_memory_max_mb()),
            })
        }
        Request::JavaRuntimes => {
            let mut versions: Vec<_> = theseus::jre::get_java_versions().await?
                .into_iter().map(|(_, version)| version).collect();
            versions.sort_by_key(|version| version.parsed_version);
            json!(versions)
        }
        Request::DetectJava { major } => {
            let major = supported_java_major(&major)?;
            json!(theseus::jre::find_filtered_jres(Some(major)).await?)
        }
        Request::SetJavaRuntime { major, path } => {
            let major = supported_java_major(&major)?;
            let version = theseus::jre::check_jre(path).await?;
            if version.parsed_version != major {
                return Err(format!("Потрібна Java {major}, знайдено Java {}.", version.parsed_version).into());
            }
            theseus::jre::set_java_version(version).await?;
            Value::Null
        }
        Request::DownloadJavaRuntime { major } => {
            let major = supported_java_major(&major)?;
            let path = theseus::jre::auto_install_java(major).await?;
            let version = theseus::jre::check_jre(path).await?;
            theseus::jre::set_java_version(version).await?;
            Value::Null
        }
        Request::ProfileLaunchSettings { id } => {
            let profile = instance::get(&id).await?.ok_or("Профіль не знайдено.")?;
            let values = profile.launch_overrides;
            json!({ "memory": values.memory.map(|value| value.maximum.to_string()), "java": values.java_path })
        }
        Request::SaveProfileLaunchSettings { id, memory, java } => {
            require_stopped(&id).await?;
            let memory = match memory.trim() {
                "" => None,
                value => {
                    let maximum: u32 = value.parse()?;
                    if maximum == 0 { return Err("Вкажіть додатній обсяг пам’яті.".into()); }
                    Some(theseus::data::MemorySettings { maximum })
                }
            };
            let java = match java.trim() {
                "" => None,
                value => {
                    theseus::jre::check_jre(PathBuf::from(value)).await?;
                    Some(value.to_owned())
                }
            };
            instance::edit(&id, theseus::data::EditInstance {
                launch_overrides: Some(theseus::data::InstanceLaunchOverridesPatch {
                    memory: Some(memory), java_path: Some(java), ..Default::default()
                }), ..Default::default()
            }).await?;
            Value::Null
        }
        Request::SaveLaunchSettings { values } => {
            if values.memory_mib == 0 || values.width == 0 || values.height == 0 {
                return Err("Пам’ять і роздільна здатність мають бути більшими за нуль.".into());
            }
            let mut settings = theseus::settings::get().await?;
            settings.memory.maximum = values.memory_mib;
            settings.force_fullscreen = values.fullscreen;
            settings.game_resolution = theseus::data::WindowSize(values.width, values.height);
            settings.extra_launch_args = values.arguments;
            theseus::settings::set(settings).await?;
            Value::Null
        }
        Request::Preferences { request } => Box::pin(preferences::handle(request)).await?,
        Request::SetDiscordPresence { enabled } => {
            theseus::settings::set_discord_presence(enabled).await?;
            Value::Null
        }
        Request::GameVersions { include_snapshots } => {
            let manifest = theseus::metadata::get_minecraft_versions().await?;
            json!(manifest.versions.into_iter()
                .filter(|version| include_snapshots || version.type_.as_str() == "release")
                .map(|version| version.id).collect::<Vec<_>>())
        }
        Request::LoaderVersions { loader, game_version } => {
            if loader == theseus::data::ModLoader::Vanilla {
                return Ok(json!([]));
            }
            let manifest = theseus::metadata::get_loader_versions(loader.as_str()).await?;
            let loaders = if let Some(entry) = manifest.game_versions.iter().find(|v| v.id == game_version) {
                if !entry.loaders.is_empty() {
                    entry.loaders.as_slice()
                } else if let Some(group_id) = entry.version_group.as_deref() {
                    manifest
                        .version_groups
                        .iter()
                        .find(|group| group.id == group_id)
                        .map(|group| group.loaders.as_slice())
                        .unwrap_or(&[])
                } else {
                    &[]
                }
            } else {
                manifest
                    .game_versions
                    .iter()
                    .find(|v| !v.loaders.is_empty())
                    .map(|v| v.loaders.as_slice())
                    .unwrap_or(&[])
            };
            let ids: Vec<_> = loaders.iter().take(100).map(|v| v.id.clone()).collect();
            json!(ids)
        }
        Request::CreateProfile { name, version, loader, loader_version } => {
            let name = name.trim();
            if name.is_empty() { return Err("Введіть назву профілю.".into()); }
            let loader_version = loader_version
                .map(|v| v.trim().to_owned())
                .filter(|v| !v.is_empty());
            install::create_instance(
                name.to_owned(), version, loader, loader_version, None, None,
                theseus::data::InstanceLink::Unmanaged,
            ).await?;
            Value::Null
        }
        Request::RenameProfile { id, name } => {
            let name = name.trim();
            if name.is_empty() { return Err("Введіть назву профілю.".into()); }
            instance::edit(&id, theseus::data::EditInstance {
                name: Some(name.to_owned()),
                ..Default::default()
            }).await?;
            Value::Null
        }
        Request::ProfileIcon { id, path } => {
            instance::edit_icon(&id, Some(&path)).await?;
            Value::Null
        }
        Request::DuplicateProfile { id } => {
            require_stopped(&id).await?;
            install::duplicate_instance(id).await?;
            Value::Null
        }
        Request::RemoveProfile { id } => {
            require_stopped(&id).await?;
            instance::remove(&id).await?;
            Value::Null
        }
        Request::Accounts => {
            let selected = minecraft_auth::get_default_user().await?;
            let mut accounts = Vec::new();
            for account in minecraft_auth::users().await? {
                let profile = &account.offline_profile;
                let is_selected = selected == Some(profile.id);
                let avatar_png = if !is_selected {
                    None
                } else if skin::is_offline(&account.access_token, &account.refresh_token) {
                    skin::offline_avatar(profile.id).ok().flatten()
                } else {
                    Box::pin(skin::online_avatar(&account)).await.ok().flatten()
                };
                accounts.push(json!({ "id": profile.id, "name": profile.name, "selected": is_selected,
                    "avatar_png": avatar_png }));
            }
            json!(accounts)
        }
        Request::UseAccount { id } => {
            minecraft_auth::set_default_user(id).await?;
            Value::Null
        }
        Request::RemoveAccount { id } => {
            minecraft_auth::remove_user(id).await?;
            Value::Null
        }
        Request::Library => {
            let running = process::get_all().await?;
            let profiles: Vec<_> = instance::list().await?.into_iter().map(|entry| {
                let profile = entry.instance;
                json!({
                    "id": profile.id,
                    "name": profile.name,
                    "icon": profile.icon_path,
                    "version": entry.applied_content_set.game_version,
                    "loader": entry.applied_content_set.loader,
                    "last_played": profile.last_played.map(|date| date.format("%Y-%m-%d").to_string()),
                    "play_seconds": profile.submitted_time_played.saturating_add(profile.recent_time_played),
                    "running": running.iter().any(|game| game.instance_id == profile.id),
                    "quarantined": entry.quarantined,
                    "is_modpack": matches!(entry.link, theseus::data::InstanceLink::ModrinthModpack { .. }),
                })
            }).collect();
            json!(profiles)
        }
        Request::QuickPlay => {
            let recent = Box::pin(worlds::get_recent_worlds(
                6, worlds::DisplayStatus::Normal | worlds::DisplayStatus::Favorite
            )).await?;
            json!(recent.into_iter().map(|entry| {
                let world = entry.world;
                let (id, server) = match world.details {
                    worlds::WorldDetails::Singleplayer { path, .. } => (path, false),
                    worlds::WorldDetails::Server { server_id, .. } => (server_id, true),
                };
                json!({ "id": id, "profile": entry.instance_id, "name": world.name, "server": server })
            }).collect::<Vec<_>>())
        }
        Request::Cover { id } => json!(
            instance::list_screenshots(&id)
                .await?
                .into_iter()
                .max_by_key(|shot| shot.modified_at)
                .map(|shot| shot.path)
        ),
        Request::Screenshots { id } => {
            let mut screenshots = instance::list_screenshots(&id).await?;
            screenshots.sort_by_key(|shot| std::cmp::Reverse(shot.modified_at));
            json!(screenshots.into_iter().map(|shot| json!({
                "filename": shot.file_name,
                "path": shot.path,
            })).collect::<Vec<_>>())
        }
        Request::ExportScreenshots { id, path } => {
            let keys: Vec<_> = instance::list_screenshots(&id).await?.into_iter()
                .map(|shot| instance::ScreenshotKey {
                    instance_id: id.clone(),
                    file_name: shot.file_name,
                }).collect();
            instance::export_screenshots(&keys, path).await?;
            Value::Null
        }
        Request::Play { id } => {
            if !process::get_by_instance_id(&id).await?.is_empty() {
                return Err(
                    "Minecraft is already running for this profile".into()
                );
            }
            Box::pin(instance::run(&id, instance::QuickPlayType::None)).await?;
            Value::Null
        }
        Request::Stop { id } => {
            instance::kill(&id).await?;
            Value::Null
        }
        Request::Log { id } => json!(logs::get_live_log_buffer(&id).await?),
        Request::LogHistory { id } => json!(logs::get_logs(&id, Some(true)).await?),
        Request::ReadLog { id, kind, filename } => {
            let path = Path::new(&filename);
            if path.file_name() != Some(path.as_os_str()) || filename.contains(['/', '\\']) {
                return Err("Неприпустима назва файлу логу.".into());
            }
            json!(logs::get_output_by_filename(&id, kind, &filename).await?)
        }
        Request::Content { id, include_pack } => {
            instance::sync_content_files(&id).await?;
            let mut items = instance::get_content_items(&id, None).await?;
            let linked = if include_pack {
                Box::pin(instance::get_linked_modpack_content(&id, None)).await?
            } else {
                Vec::new()
            };
            for mut item in linked {
                let installed = items.iter_mut().find(|installed| installed.id == item.id
                    || installed.project.as_ref().zip(item.project.as_ref()).is_some_and(|(a, b)| a.id == b.id)
                    && installed.version.as_ref().zip(item.version.as_ref()).is_some_and(|(a, b)| a.id == b.id));
                if let Some(installed) = installed {
                    installed.source_kind = item.source_kind.or(installed.source_kind);
                } else {
                    item.locked = true;
                    item.file_path.clear();
                    items.push(item);
                }
            }
            items.sort_by_cached_key(|item| item.file_name.to_lowercase());
            json!(items.into_iter().map(|item| {
                let can_choose_version = item.project_type == theseus::data::ProjectType::Mod
                    && item.project.is_some() && !item.locked && item.synced_pack.is_none();
                let name = item.project.map(|project| project.title)
                    .or_else(|| item.embedded_metadata.as_ref().and_then(|meta| meta.name.clone()))
                    .unwrap_or_else(|| item.file_name.clone());
                let version = item.version.map(|version| version.version_number)
                    .or_else(|| item.embedded_metadata.and_then(|meta| meta.version));
                json!({
                    "entry_id": item.id,
                    "path": item.file_path,
                    "name": name,
                    "file": item.file_name,
                    "version": version,
                    "enabled": item.enabled,
                    "locked": item.locked,
                    "has_update": item.has_update,
                    "synced": item.synced_pack.is_some(),
                    "pack_default": item.file_path.is_empty() || matches!(item.source_kind.map(|kind| kind.as_str()),
                        Some("modrinth_modpack" | "imported_modpack" | "shared_instance" | "server_project")),
                    "kind": item.project_type,
                    "can_choose_version": can_choose_version,
                })
            }).collect::<Vec<_>>())
        }
        Request::AddMod { id, path } => {
            require_stopped(&id).await?;
            if !path.extension().is_some_and(|ext| ext.eq_ignore_ascii_case("jar")) {
                return Err("Виберіть файл мода .jar.".into());
            }
            instance::add_project_from_path(&id, &path, Some(theseus::data::ProjectType::Mod)).await?;
            Value::Null
        }
        Request::AddContent { id, path, kind } => {
            require_stopped(&id).await?;
            let expected_ext = match kind {
                theseus::data::ProjectType::Mod => "jar",
                theseus::data::ProjectType::ResourcePack
                | theseus::data::ProjectType::ShaderPack
                | theseus::data::ProjectType::DataPack => "zip",
            };
            if !path.extension().is_some_and(|ext| ext.eq_ignore_ascii_case(expected_ext)) {
                return Err(format!("Виберіть файл .{expected_ext}.").into());
            }
            instance::add_project_from_path(&id, &path, Some(kind)).await?;
            Value::Null
        }
        Request::SetContentEnabled { id, path, action } => {
            require_stopped(&id).await?;
            instance::toggle_disable_project(&id, &path, Some(matches!(action, ContentAction::Enable))).await?;
            Value::Null
        }
        Request::RemoveContent { id, path } => {
            require_stopped(&id).await?;
            instance::remove_project(&id, &path).await?;
            Value::Null
        }
        Request::Worlds { id } => json!(
            worlds::get_instance_worlds(&id).await?.into_iter()
                .filter(|world| matches!(&world.details, worlds::WorldDetails::Singleplayer { .. }))
                .map(|world| json!({
                    "id": world.world_id(),
                    "name": world.name,
                    "last_played": world.last_played.map(|date| date.format("%Y-%m-%d").to_string()),
                }))
                .collect::<Vec<_>>()
        ),
        Request::Servers { id } => json!(worlds::get_instance_worlds(&id).await?
            .into_iter().filter_map(|world| {
                let worlds::WorldDetails::Server { server_id, address, source, .. } = world.details else { return None; };
                Some(json!({
                    "id": server_id, "name": world.name, "address": address,
                    "editable": matches!(source, instance::ServerSource::UserSynced | instance::ServerSource::LocalDesynced),
                }))
            }).collect::<Vec<_>>()),
        Request::SaveServer { id, server, name, address } => {
            require_stopped(&id).await?;
            let (name, address) = (name.trim().to_owned(), address.trim().to_owned());
            if name.is_empty() { return Err("Введіть назву сервера.".into()); }
            theseus::server_address::parse_server_address(&address)?;
            if let Some(server) = server {
                let (index, _, status) = find_server(&id, &server, true).await?;
                worlds::edit_server_in_instance(&id, index, name, address, status).await?;
            } else {
                worlds::add_server_to_instance(&id, name, address, worlds::ServerPackStatus::Prompt, None, None).await?;
            }
            Value::Null
        }
        Request::RemoveServer { id, server } => {
            require_stopped(&id).await?;
            let (index, _, _) = find_server(&id, &server, true).await?;
            worlds::remove_server_from_instance(&id, index).await?;
            Value::Null
        }
        Request::JoinServer { id, server } => {
            require_stopped(&id).await?;
            let (_, address, _) = find_server(&id, &server, false).await?;
            Box::pin(instance::run(&id, instance::QuickPlayType::Server(
                theseus::server_address::ServerAddress::Unresolved(address)
            ))).await?;
            Value::Null
        }
        Request::OpenWorld { id, world } => {
            require_stopped(&id).await?;
            let world = local_world(&id, &world).await?;
            Box::pin(instance::run(&id, instance::QuickPlayType::Singleplayer(world.world_id().to_owned()))).await?;
            Value::Null
        }
        Request::BackupWorld { id, world } => {
            require_stopped(&id).await?;
            let world = local_world(&id, &world).await?;
            let directory = instance::get_full_path(&id).await?;
            json!(worlds::backup_world(&directory, world.world_id()).await?)
        }
        Request::Queue => {
            let jobs: Vec<_> = install::list_jobs(true).await?.into_iter().map(|job| {
                json!({
                    "job_id": job.job_id,
                    "instance_id": job.instance_id,
                    "status": job.status,
                    "phase": job.phase,
                    "paused": job.paused,
                    "can_pause": job.can_pause,
                    "can_cancel": job.can_cancel,
                    "progress": job.progress.filter(|progress| progress.total > 0)
                        .map(|progress| (progress.current as f64 / progress.total as f64).min(1.0)),
                    "error": job.error.map(|error| error.message),
                })
            }).collect();
            json!(jobs)
        }
        Request::Import { path } => json!(
            install::create_modpack_instance(
                theseus::pack::install_from::CreatePackLocation::FromFile {
                    path
                },
                None
            )
            .await?
        ),
        Request::Job { id, action } => json!(match action {
            JobAction::Pause => install::pause_job(id).await?,
            JobAction::Resume => install::resume_job(id).await?,
            JobAction::Cancel => install::cancel_job(id).await?,
            JobAction::Retry => install::retry_job(id).await?,
        }),
        Request::OfflineLogin { name } => {
            let account = minecraft_auth::login_offline(name.trim()).await?;
            minecraft_auth::set_default_user(account.offline_profile.id)
                .await?;
            Value::Null
        }
        Request::BeginLogin => {
            let flow = minecraft_auth::begin_login().await?;
            let url = json!(&flow.auth_request_uri);
            *login = Some((Instant::now(), flow));
            url
        }
        Request::FinishLogin { code } => {
            let (started, flow) =
                login.take().ok_or("No active Microsoft login")?;
            if started.elapsed() > Duration::from_secs(600) {
                return Err("Microsoft login expired".into());
            }
            let account = minecraft_auth::finish_login(&code, flow).await?;
            minecraft_auth::set_default_user(account.offline_profile.id)
                .await?;
            Value::Null
        }
        Request::CancelLogin => {
            *login = None;
            Value::Null
        }
    })
}

fn reply(stdout: &mut impl Write, value: Value) -> io::Result<()> {
    writeln!(stdout, "{value}")?;
    stdout.flush()
}

#[tokio::main]
async fn main() -> Result<()> {
    let identifier = "com.gradient.launcher.native";
    let directory =
        theseus::data::DirectoryInfo::initial_settings_dir_path(identifier)
            .ok_or("Gradient data directory is unavailable")?;
    std::fs::create_dir_all(&directory)?;
    let lock = OpenOptions::new()
        .read(true)
        .write(true)
        .create(true)
        .truncate(false)
        .open(directory.join("gradient-native.lock"))?;
    lock.try_lock().map_err(|error| {
        format!("Could not lock Gradient data directory: {error}")
    })?;

    EventState::init().await?;
    State::init(identifier.to_owned()).await?;

    let mut login = None;
    reply(&mut io::stdout().lock(), json!({ "protocol": 1 }))?;
    for line in io::stdin().lock().lines() {
        let response = match serde_json::from_str::<Request>(&line?) {
            Ok(request) => match handle(request, &mut login).await {
                Ok(value) => json!({ "ok": value }),
                Err(error) => json!({ "error": error.to_string() }),
            },
            Err(error) => json!({ "error": error.to_string() }),
        };
        match reply(&mut io::stdout().lock(), response) {
            Ok(()) => {}
            Err(error) if error.kind() == io::ErrorKind::BrokenPipe => break,
            Err(error) => return Err(error.into()),
        }
    }
    // Keep game output and active installations alive after the UI closes.
    loop {
        let installs_running =
            install::list_jobs(false).await?.iter().any(|job| {
                !job.paused
                    && matches!(
                        job.status,
                        install::InstallJobStatus::Queued
                            | install::InstallJobStatus::Running
                    )
            });
        if !installs_running && process::get_all().await?.is_empty() {
            break;
        }
        tokio::time::sleep(Duration::from_secs(1)).await;
    }
    Ok(())
}
