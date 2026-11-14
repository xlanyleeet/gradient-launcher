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

type Result<T> = std::result::Result<T, Box<dyn Error>>;

#[derive(Deserialize)]
#[serde(tag = "method", rename_all = "snake_case")]
enum Request {
    Library,
    Cover {
        id: String,
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
        Request::Library => {
            let running = process::get_all().await?;
            let profiles: Vec<_> = instance::list().await?.into_iter().map(|entry| {
                let profile = entry.instance;
                json!({
                    "id": profile.id,
                    "name": profile.name,
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
        Request::Content { id } => {
            instance::sync_content_files(&id).await?;
            let mut items = instance::get_content_items(&id, None).await?;
            items.sort_by_cached_key(|item| item.file_name.to_lowercase());
            json!(items.into_iter().map(|item| {
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
                    "synced": item.synced_pack.is_some(),
                    "kind": item.project_type,
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
