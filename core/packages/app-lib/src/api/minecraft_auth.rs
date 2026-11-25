//! Authentication flow interface

use reqwest::StatusCode;

use crate::State;
use crate::state::{Credentials, MinecraftLoginFlow};
use crate::util::fetch::INSECURE_REQWEST_CLIENT;

#[tracing::instrument]
pub async fn check_reachable() -> crate::Result<()> {
    let resp = INSECURE_REQWEST_CLIENT
        .get("https://sessionserver.mojang.com/session/minecraft/hasJoined")
        .send()
        .await?;
    if resp.status() == StatusCode::NO_CONTENT {
        return Ok(());
    }
    resp.error_for_status()?;
    Ok(())
}

#[tracing::instrument]
pub async fn begin_login() -> crate::Result<MinecraftLoginFlow> {
    let state = State::get().await?;

    crate::state::login_begin(&state.pool).await
}

#[tracing::instrument]
pub async fn finish_login(
    code: &str,
    flow: MinecraftLoginFlow,
) -> crate::Result<Credentials> {
    let state = State::get().await?;

    let credentials =
        crate::state::login_finish(code, flow, &state.pool).await?;

    if let Err(error) =
        crate::onboarding_checklist::mark_logged_into_minecraft().await
    {
        tracing::warn!(
            "Failed to mark Minecraft login in onboarding checklist: {error}"
        );
    }

    Ok(credentials)
}

#[tracing::instrument]
pub async fn get_default_user() -> crate::Result<Option<uuid::Uuid>> {
    let state = State::get().await?;
    let user = Credentials::get_default_credential(&state.pool).await?;
    Ok(user.map(|user| user.offline_profile.id))
}

#[tracing::instrument]
pub async fn set_default_user(user: uuid::Uuid) -> crate::Result<()> {
    let state = State::get().await?;
    let users = Credentials::get_all(&state.pool).await?;
    let (_, mut user) = users.remove(&user).ok_or_else(|| {
        crate::ErrorKind::OtherError(format!(
            "Tried to get nonexistent user with ID {user}"
        ))
        .as_error()
    })?;

    user.active = true;
    user.upsert(&state.pool).await?;

    Ok(())
}

/// Remove a user account from the database
#[tracing::instrument]
pub async fn remove_user(uuid: uuid::Uuid) -> crate::Result<()> {
    let state = State::get().await?;

    let users = Credentials::get_all(&state.pool).await?;

    if let Some((uuid, user)) = users.remove(&uuid) {
        Credentials::remove(uuid, &state.pool).await?;

        if user.active
            && let Some((_, mut user)) = users.into_iter().next()
        {
            user.active = true;
            user.upsert(&state.pool).await?;
        }
    }

    Ok(())
}

/// Get a copy of the list of all user credentials
#[tracing::instrument]
pub async fn users() -> crate::Result<Vec<Credentials>> {
    let state = State::get().await?;
    let users = Credentials::get_all(&state.pool).await?;
    Ok(users.into_iter().map(|x| x.1).collect())
}

fn validate_offline_username(username: &str) -> crate::Result<&str> {
    let clean_name = username.trim();
    let valid = (3..=16).contains(&clean_name.len())
        && clean_name
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || byte == b'_');
    if !valid {
        return Err(crate::ErrorKind::InputError(
            "Offline username must be 3-16 characters and contain only letters, numbers, and underscores.".to_string(),
        )
        .into());
    }
    Ok(clean_name)
}

/// Adds or logs in an offline player account (pirated / nickname mode)
#[tracing::instrument]
pub async fn login_offline(username: &str) -> crate::Result<Credentials> {
    let name = validate_offline_username(username)?;
    let creds = Credentials::create_offline(name);
    let state = State::get().await?;
    creds.upsert(&state.pool).await?;
    Ok(creds)
}

#[cfg(test)]
mod tests {
    use super::validate_offline_username;

    #[test]
    fn offline_username_validation() {
        assert_eq!(validate_offline_username("  Steve_123 ").unwrap(), "Steve_123");
        assert!(validate_offline_username("ab").is_err());
        assert!(validate_offline_username("12345678901234567").is_err());
        assert!(validate_offline_username("bad-name").is_err());
        assert!(validate_offline_username("bad name").is_err());
        assert!(validate_offline_username("").is_err());
    }
}
