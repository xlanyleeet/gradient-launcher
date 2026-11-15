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

type Result<T> = std::result::Result<T, Box<dyn Error>>;

#[derive(Deserialize)]
#[serde(tag = "method", rename_all = "snake_case")]
enum Request {
    Library,
    UploadSkin {
        path: PathBuf,
        variant: SkinVariant,
    },
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
    GameVersions,
    CreateProfile {
        name: String,
        version: String,
        loader: theseus::data::ModLoader,
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
    },
    AddMod {
        id: String,
        path: PathBuf,
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
        Request::UploadSkin { path, variant } => {
            use std::io::Read;
            const LIMIT: u64 = 1024 * 1024;
            let file = std::fs::File::open(&path)?;
            if !file.metadata()?.is_file() {
                return Err("Виберіть PNG-файл скіна.".into());
            }
            let mut bytes = Vec::new();
            file.take(LIMIT + 1).read_to_end(&mut bytes)?;
            if bytes.len() as u64 > LIMIT || !bytes.starts_with(b"\x89PNG\r\n\x1a\n") {
                return Err("Виберіть PNG-файл скіна розміром до 1 МіБ.".into());
            }
            let variant = match variant {
                SkinVariant::Classic => theseus::minecraft_skins::MinecraftSkinVariant::Classic,
                SkinVariant::Slim => theseus::minecraft_skins::MinecraftSkinVariant::Slim,
            };
            let cape = Box::pin(theseus::minecraft_skins::get_available_capes())
                .await?.into_iter().find(|cape| cape.is_equipped);
            Box::pin(theseus::minecraft_skins::add_and_equip_custom_skin(bytes.into(), variant, cape)).await?;
            Box::pin(theseus::minecraft_skins::flush_pending_skin_change()).await?;
            Value::Null
        }
        Request::ResetSkin => {
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
        Request::Discover { request } => discover::handle(request).await?,
        Request::LaunchSettings => {
            let settings = theseus::settings::get().await?;
            json!(LaunchSettings {
                memory_mib: settings.memory.maximum,
                fullscreen: settings.force_fullscreen,
                width: settings.game_resolution.0,
                height: settings.game_resolution.1,
                arguments: settings.extra_launch_args,
            })
        }
        Request::JavaRuntimes => {
            let mut versions: Vec<_> = theseus::jre::get_java_versions().await?
                .into_iter().map(|(_, version)| version).collect();
            versions.sort_by_key(|version| version.parsed_version);
            json!(versions)
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
        Request::GameVersions => {
            let manifest = theseus::metadata::get_minecraft_versions().await?;
            json!(manifest.versions.into_iter()
                .filter(|version| version.type_.as_str() == "release")
                .map(|version| version.id).collect::<Vec<_>>())
        }
        Request::CreateProfile { name, version, loader } => {
            let name = name.trim();
            if name.is_empty() { return Err("Введіть назву профілю.".into()); }
            install::create_instance(
                name.to_owned(), version, loader, None, None, None,
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
            json!(minecraft_auth::users().await?.into_iter().map(|account| {
                let profile = account.offline_profile;
                json!({ "id": profile.id, "name": profile.name, "selected": selected == Some(profile.id) })
            }).collect::<Vec<_>>())
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
                    "running": running.iter().any(|game| game.instance_id == profile.id),
                    "quarantined": entry.quarantined,
                })
            }).collect();
            json!(profiles)
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
        Request::Content { id } => {
            instance::sync_content_files(&id).await?;
            let mut items = instance::get_content_items(&id, None).await?;
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
                    "path": item.file_path,
                    "name": name,
                    "file": item.file_name,
                    "version": version,
                    "enabled": item.enabled,
                    "locked": item.locked,
                    "has_update": item.has_update,
                    "synced": item.synced_pack.is_some(),
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
