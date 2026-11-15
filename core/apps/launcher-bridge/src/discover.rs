use serde::Deserialize;
use serde_json::{Value, json};
use theseus::{
    cache, data, install, instance, pack::install_from::CreatePackLocation,
};

#[derive(Deserialize)]
#[serde(rename_all = "snake_case")]
pub(super) enum Kind {
    Mod,
    Modpack,
}

impl Kind {
    fn key(&self) -> &'static str {
        match self {
            Self::Mod => "mod",
            Self::Modpack => "modpack",
        }
    }
}

#[derive(Deserialize)]
#[serde(tag = "action", rename_all = "snake_case")]
pub(super) enum Request {
    Search {
        query: String,
        kind: Kind,
        profile: Option<String>,
        offset: Option<String>,
        sort: Option<Sort>,
        game_version: Option<String>,
        loader: Option<data::ModLoader>,
    },
    Versions {
        project: String,
        kind: Kind,
        profile: Option<String>,
    },
    Project {
        project: String,
        kind: Kind,
    },
    ResolveLink {
        url: String,
    },
    Install {
        project: String,
        version: String,
        kind: Kind,
        profile: Option<String>,
    },
}

#[derive(Deserialize)]
#[serde(rename_all = "snake_case")]
pub(super) enum Sort {
    Relevance,
    Downloads,
    Newest,
    Updated,
}

impl Sort {
    fn index(self) -> &'static str {
        match self {
            Self::Relevance => "relevance",
            Self::Downloads => "downloads",
            Self::Newest => "newest",
            Self::Updated => "updated",
        }
    }
}

async fn target(
    kind: &Kind,
    profile: Option<String>,
) -> super::Result<Option<data::InstanceMetadata>> {
    match kind {
        Kind::Modpack => Ok(None),
        Kind::Mod => {
            let id =
                profile.ok_or("Виберіть профіль для встановлення мода.")?;
            Ok(Some(
                instance::get(&id).await?.ok_or("Профіль не знайдено.")?,
            ))
        }
    }
}

fn compatible(
    version: &data::Version,
    target: Option<&data::InstanceMetadata>,
) -> bool {
    target.is_none_or(|profile| {
        let content = &profile.applied_content_set;
        version.game_versions.contains(&content.game_version)
            && version
                .loaders
                .iter()
                .any(|loader| loader == content.loader.as_str())
    })
}

pub(super) async fn installed_mod_versions(
    id: &str,
    path: &str,
) -> super::Result<Value> {
    let (profile, item) = Box::pin(installed_mod(id, path)).await?;
    let project = item.project.ok_or("Мод не пов’язаний із Modrinth.")?;
    let current_id = item.version.map(|version| version.id);
    let mut versions = Box::pin(cache::get_project_versions(
        &project.id,
        Some(data::CacheBehaviour::MustRevalidate),
    ))
    .await?
    .unwrap_or_default();
    versions.sort_by_key(|version| std::cmp::Reverse(version.date_published));
    let options = versions
        .into_iter()
        .filter(|version| compatible(version, Some(&profile)))
        .map(|version| {
            json!({
                "id": version.id,
                "name": version.version_number,
                "channel": version.version_type,
            })
        })
        .collect::<Vec<_>>();
    Ok(json!({ "current_id": current_id, "options": options }))
}

pub(super) async fn switch_installed_mod(
    id: &str,
    path: &str,
    version: &str,
) -> super::Result<()> {
    super::require_stopped(id).await?;
    let (profile, item) = Box::pin(installed_mod(id, path)).await?;
    let project = item.project.ok_or("Мод не пов’язаний із Modrinth.")?;
    let selected = cache::get_version(version, None)
        .await?
        .ok_or("Версію не знайдено.")?;
    if selected.project_id != project.id
        || !compatible(&selected, Some(&profile))
    {
        return Err("Версія несумісна або належить іншому моду.".into());
    }
    if item.version.is_some_and(|current| current.id == version) {
        return Ok(());
    }
    Box::pin(instance::switch_project_version_with_dependencies(
        id, path, version,
    ))
    .await?;
    Ok(())
}

async fn installed_mod(
    id: &str,
    path: &str,
) -> super::Result<(data::InstanceMetadata, data::ContentItem)> {
    let profile = instance::get(id).await?.ok_or("Профіль не знайдено.")?;
    let item = Box::pin(instance::get_content_items(id, None))
        .await?
        .into_iter()
        .find(|item| {
            item.file_path == path
                && item.project_type == data::ProjectType::Mod
        })
        .ok_or("Мод не знайдено в профілі.")?;
    if profile.quarantined || item.locked || item.synced_pack.is_some() {
        return Err("Версію цього мода не можна змінити.".into());
    }
    Ok((profile, item))
}

pub(super) async fn handle(request: Request) -> super::Result<Value> {
    Ok(match request {
        Request::Search {
            query,
            kind,
            profile,
            offset,
            sort,
            game_version,
            loader,
        } => {
            let target = target(&kind, profile).await?;
            let offset = offset
                .map(|offset| offset.parse::<u32>())
                .transpose()?
                .unwrap_or(0);
            let mut filters =
                vec![vec![format!("project_type:{}", kind.key())]];
            if let Some(profile) = target {
                filters.push(vec![format!(
                    "versions:{}",
                    profile.applied_content_set.game_version
                )]);
                filters.push(vec![format!(
                    "categories:{}",
                    profile.applied_content_set.loader.as_str()
                )]);
                filters.push(vec![
                    "environment:client_and_server".to_owned(),
                    "environment:client_only".to_owned(),
                    "environment:client_only_server_optional".to_owned(),
                    "environment:singleplayer_only".to_owned(),
                    "environment:server_only_client_optional".to_owned(),
                    "environment:client_or_server".to_owned(),
                    "environment:client_or_server_prefers_both".to_owned(),
                ]);
            } else {
                if let Some(version) = game_version
                    .as_deref()
                    .map(str::trim)
                    .filter(|value| !value.is_empty())
                {
                    if version.len() > 40
                        || !version.bytes().all(|byte| {
                            byte.is_ascii_alphanumeric()
                                || b".-_".contains(&byte)
                        })
                    {
                        return Err(
                            "Введіть версію Minecraft, наприклад 1.21.1."
                                .into(),
                        );
                    }
                    filters.push(vec![format!("versions:{version}")]);
                }
                if let Some(loader) = loader {
                    filters
                        .push(vec![format!("categories:{}", loader.as_str())]);
                }
            }
            let facets = serde_json::to_string(&filters)?;
            let offset_param = offset.to_string();
            let query = query.trim();
            let query = url::form_urlencoded::Serializer::new(String::new())
                .extend_pairs([
                    ("query", query),
                    (
                        "index",
                        sort.unwrap_or(if query.is_empty() {
                            Sort::Downloads
                        } else {
                            Sort::Relevance
                        })
                        .index(),
                    ),
                    ("facets", facets.as_str()),
                    ("offset", offset_param.as_str()),
                    ("limit", "20"),
                ])
                .finish();
            let Some(results) =
                cache::get_search_results(&format!("?{query}"), None).await?
            else {
                return Err("Modrinth не повернув результати пошуку.".into());
            };
            let hits: Vec<_> = results.result.hits.into_iter().map(|hit| json!({
                "id": hit.project_id, "name": hit.title, "description": hit.description, "author": hit.author,
                "icon": hit.icon_url,
            })).collect();
            let next_offset = offset.saturating_add(hits.len() as u32);
            json!({
                "hits": hits,
                "next_offset": (next_offset > offset && next_offset < results.result.total_hits).then_some(next_offset),
            })
        }
        Request::Versions {
            project,
            kind,
            profile,
        } => {
            let target = target(&kind, profile).await?;
            let mut versions = cache::get_project_versions(&project, None)
                .await?
                .unwrap_or_default();
            versions.sort_by_key(|version| {
                std::cmp::Reverse(version.date_published)
            });
            json!(versions.into_iter().filter(|version| compatible(version, target.as_ref()))
                .map(|version| json!({ "id": version.id, "name": version.version_number, "channel": version.version_type }))
                .collect::<Vec<_>>())
        }
        Request::Project { project, kind } => {
            let project = Box::pin(cache::get_project(&project, None))
                .await?
                .ok_or("Проєкт Modrinth не знайдено.")?;
            if project.project_type != kind.key() {
                return Err(
                    "Проєкт не належить до вибраного типу контенту.".into()
                );
            }
            let mut url = url::Url::parse("https://modrinth.com/")?;
            url.path_segments_mut()
                .map_err(|_| "Не вдалося побудувати адресу проєкту.")?
                .extend([
                    kind.key(),
                    project.slug.as_deref().unwrap_or(&project.id),
                ]);
            let gallery = project
                .gallery
                .iter()
                .find(|image| image.featured)
                .or_else(|| project.gallery.first());
            let body: String = project.body.chars().take(32_768).collect();
            json!({
                "description": project.description,
                "body": body,
                "license": project.license.name,
                "downloads": project.downloads,
                "updated": project.updated.format("%Y-%m-%d").to_string(),
                "url": url.as_str(),
                "gallery": gallery.map(|image| &image.url),
            })
        }
        Request::ResolveLink { url } => {
            Box::pin(resolve_project_link(&url)).await?
        }
        Request::Install {
            project,
            version,
            kind,
            profile,
        } => {
            let target = target(&kind, profile).await?;
            let project_data = cache::get_project(&project, None)
                .await?
                .ok_or("Проєкт не знайдено.")?;
            let selected = cache::get_version(&version, None)
                .await?
                .ok_or("Версію не знайдено.")?;
            if project_data.project_type != kind.key()
                || selected.project_id != project
                || !compatible(&selected, target.as_ref())
            {
                return Err(
                    "Версія несумісна або належить іншому проєкту.".into()
                );
            }
            if let Some(profile) = target {
                super::require_stopped(&profile.instance.id).await?;
                Box::pin(instance::install_project_and_wait(
                    &profile.instance.id,
                    instance::InstallProjectWithDependenciesRequest {
                        project_id: project,
                        version_id: Some(version),
                        content_type: data::ContentType::Mod,
                        selected: Default::default(),
                    },
                ))
                .await?;
            } else {
                install::create_modpack_instance(
                    CreatePackLocation::FromVersionId {
                        project_id: project,
                        version_id: version,
                        title: project_data.title,
                        icon_url: project_data.icon_url,
                    },
                    None,
                )
                .await?;
            }
            Value::Null
        }
    })
}

async fn resolve_project_link(value: &str) -> super::Result<Value> {
    let url = url::Url::parse(value.trim())?;
    if !url.username().is_empty()
        || url.password().is_some()
        || url.port().is_some()
    {
        return Err("Непідтримуване посилання.".into());
    }
    let host = url.host_str().ok_or("Посилання не має адреси.")?;
    let mut parts: Vec<_> = url
        .path_segments()
        .ok_or("Непідтримуване посилання.")?
        .filter(|part| !part.is_empty())
        .collect();
    match (url.scheme(), host) {
        ("gradient-native" | "gradient" | "modrinth", _) => {
            parts.insert(0, host)
        }
        ("https", "modrinth.com" | "www.modrinth.com") => {}
        _ => {
            return Err(
                "Вставте посилання на мод, модпак або версію Modrinth.".into()
            );
        }
    }
    let valid_id = |id: &str| {
        !id.is_empty()
            && id.len() <= 128
            && id.bytes().all(|byte| {
                byte.is_ascii_alphanumeric() || b"-_".contains(&byte)
            })
    };
    let (project_key, requested_kind, preferred_version) = match parts
        .as_slice()
    {
        [kind @ ("mod" | "modpack"), id] if valid_id(id) => {
            (id.to_string(), Some(*kind), None)
        }
        [kind @ ("mod" | "modpack"), id, "version", version]
            if valid_id(id) && valid_id(version) =>
        {
            (id.to_string(), Some(*kind), Some(version.to_string()))
        }
        ["version", id] if valid_id(id) => {
            let version = Box::pin(cache::get_version(id, None))
                .await?
                .ok_or("Версію не знайдено.")?;
            (version.project_id, None, Some(version.id))
        }
        _ => {
            return Err(
                "Вставте посилання на мод, модпак або версію Modrinth.".into()
            );
        }
    };
    let project = Box::pin(cache::get_project(&project_key, None))
        .await?
        .ok_or("Проєкт Modrinth не знайдено.")?;
    let kind = project.project_type.as_str();
    if !matches!(kind, "mod" | "modpack")
        || requested_kind.is_some_and(|requested| requested != kind)
    {
        return Err("Посилання має вказувати на мод або модпак.".into());
    }
    Ok(json!({
        "id": project.id, "name": project.title, "description": project.description,
        "kind": kind, "preferred_version": preferred_version,
    }))
}
