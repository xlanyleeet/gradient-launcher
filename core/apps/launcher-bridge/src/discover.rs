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
    },
    Versions {
        project: String,
        kind: Kind,
        profile: Option<String>,
    },
    Install {
        project: String,
        version: String,
        kind: Kind,
        profile: Option<String>,
    },
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

pub(super) async fn handle(request: Request) -> super::Result<Value> {
    Ok(match request {
        Request::Search {
            query,
            kind,
            profile,
            offset,
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
                    "client_side:required".to_owned(),
                    "client_side:optional".to_owned(),
                ]);
            }
            let facets = serde_json::to_string(&filters)?;
            let offset_param = offset.to_string();
            let query = url::form_urlencoded::Serializer::new(String::new())
                .extend_pairs([
                    ("query", query.trim()),
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
