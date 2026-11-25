use base64::{Engine, engine::general_purpose::STANDARD};

fn is_sentinel(value: &str) -> bool {
    value.is_empty() || value == "0" || value == "offline"
}

/// Encrypts a sensitive token for at-rest storage in SQLite.
///
/// Sentinels (`""`, `"0"`, `"offline"`) are returned untouched.
pub(crate) fn protect_token(plaintext: &str) -> String {
    if is_sentinel(plaintext) {
        return plaintext.to_owned();
    }
    protect_platform(plaintext)
}

/// Decrypts a token previously encrypted by [`protect_token`].
///
/// Legacy plaintext values without a recognized prefix and sentinels are
/// returned as-is for backwards compatibility.
pub(crate) fn unprotect_token(stored: &str) -> String {
    if is_sentinel(stored) {
        return stored.to_owned();
    }
    unprotect_platform(stored)
}

#[cfg(windows)]
fn protect_platform(plaintext: &str) -> String {
    use windows::Win32::Foundation::{HLOCAL, LocalFree};
    use windows::Win32::Security::Cryptography::{
        CRYPT_INTEGER_BLOB, CRYPTPROTECT_UI_FORBIDDEN, CryptProtectData,
    };

    let mut input_bytes = plaintext.as_bytes().to_vec();
    let in_blob = CRYPT_INTEGER_BLOB {
        cbData: match u32::try_from(input_bytes.len()) {
            Ok(len) => len,
            Err(_) => return plaintext.to_owned(),
        },
        pbData: input_bytes.as_mut_ptr(),
    };
    let mut out_blob = CRYPT_INTEGER_BLOB::default();

    // SAFETY: `in_blob` points to valid bytes for the duration of the call and
    // `out_blob` is initialized by `CryptProtectData` and freed with `LocalFree`.
    let result = unsafe {
        CryptProtectData(
            &in_blob,
            windows_core::PCWSTR::null(),
            None,
            None,
            None,
            CRYPTPROTECT_UI_FORBIDDEN,
            &mut out_blob,
        )
    };
    if let Err(err) = result {
        tracing::warn!("DPAPI token encryption failed, storing plaintext: {err}");
        return plaintext.to_owned();
    }

    // SAFETY: `CryptProtectData` succeeded so `out_blob.pbData` is valid for `cbData` bytes.
    let encrypted = unsafe {
        let slice = std::slice::from_raw_parts(
            out_blob.pbData,
            out_blob.cbData as usize,
        );
        let copy = slice.to_vec();
        let _ = LocalFree(Some(HLOCAL(out_blob.pbData.cast())));
        copy
    };

    format!("dpapi:v1:{}", STANDARD.encode(encrypted))
}

#[cfg(windows)]
fn unprotect_platform(stored: &str) -> String {
    use windows::Win32::Foundation::{HLOCAL, LocalFree};
    use windows::Win32::Security::Cryptography::{
        CRYPT_INTEGER_BLOB, CRYPTPROTECT_UI_FORBIDDEN, CryptUnprotectData,
    };

    let Some(b64) = stored.strip_prefix("dpapi:v1:") else {
        return stored.to_owned();
    };
    let Ok(mut encrypted) = STANDARD.decode(b64) else {
        tracing::warn!("Failed to base64-decode DPAPI-protected token");
        return stored.to_owned();
    };
    let Ok(len) = u32::try_from(encrypted.len()) else {
        return stored.to_owned();
    };

    let in_blob = CRYPT_INTEGER_BLOB {
        cbData: len,
        pbData: encrypted.as_mut_ptr(),
    };
    let mut out_blob = CRYPT_INTEGER_BLOB::default();

    // SAFETY: `in_blob` points to valid bytes for the duration of the call and
    // `out_blob` is initialized by `CryptUnprotectData` and freed with `LocalFree`.
    let result = unsafe {
        CryptUnprotectData(
            &in_blob,
            None,
            None,
            None,
            None,
            CRYPTPROTECT_UI_FORBIDDEN,
            &mut out_blob,
        )
    };
    if let Err(err) = result {
        tracing::warn!("DPAPI token decryption failed: {err}");
        return stored.to_owned();
    }

    // SAFETY: `CryptUnprotectData` succeeded so `out_blob.pbData` is valid for `cbData` bytes.
    let decrypted = unsafe {
        let slice = std::slice::from_raw_parts(
            out_blob.pbData,
            out_blob.cbData as usize,
        );
        let copy = slice.to_vec();
        let _ = LocalFree(Some(HLOCAL(out_blob.pbData.cast())));
        copy
    };

    String::from_utf8(decrypted).unwrap_or_else(|_| {
        tracing::warn!("DPAPI-decrypted token is not valid UTF-8");
        stored.to_owned()
    })
}

#[cfg(target_os = "macos")]
mod macos_keychain {
    use std::ffi::c_void;
    use std::ptr;
    use std::sync::OnceLock;

    const SERVICE: &[u8] = b"com.gradient.launcher.native";
    const ACCOUNT: &[u8] = b"token-encryption-key";
    const ERR_SEC_SUCCESS: i32 = 0;
    const ERR_SEC_DUPLICATE_ITEM: i32 = -25299;
    const ERR_SEC_ITEM_NOT_FOUND: i32 = -25300;

    #[link(name = "Security", kind = "framework")]
    unsafe extern "C" {
        fn SecKeychainFindGenericPassword(
            keychain_or_array: *const c_void,
            service_name_length: u32,
            service_name: *const u8,
            account_name_length: u32,
            account_name: *const u8,
            password_length: *mut u32,
            password_data: *mut *mut c_void,
            item_ref: *mut *mut c_void,
        ) -> i32;

        fn SecKeychainAddGenericPassword(
            keychain: *const c_void,
            service_name_length: u32,
            service_name: *const u8,
            account_name_length: u32,
            account_name: *const u8,
            password_length: u32,
            password_data: *const c_void,
            item_ref: *mut *mut c_void,
        ) -> i32;

        fn SecKeychainItemFreeContent(
            attr_list: *const c_void,
            data: *mut c_void,
        ) -> i32;
    }

    fn find_key() -> Result<Option<[u8; 32]>, i32> {
        let mut len: u32 = 0;
        let mut data: *mut c_void = ptr::null_mut();
        // SAFETY: Passing valid pointers to CoreServices/Security framework C API.
        let status = unsafe {
            SecKeychainFindGenericPassword(
                ptr::null(),
                SERVICE.len() as u32,
                SERVICE.as_ptr(),
                ACCOUNT.len() as u32,
                ACCOUNT.as_ptr(),
                &mut len,
                &mut data,
                ptr::null_mut(),
            )
        };
        if status == ERR_SEC_ITEM_NOT_FOUND {
            return Ok(None);
        }
        if status != ERR_SEC_SUCCESS {
            return Err(status);
        }
        let result = if !data.is_null() && len == 32 {
            let mut key = [0u8; 32];
            // SAFETY: `data` is valid for `len` bytes per Security.framework contract.
            unsafe {
                ptr::copy_nonoverlapping(data.cast::<u8>(), key.as_mut_ptr(), 32);
            }
            Ok(Some(key))
        } else {
            Ok(None)
        };
        if !data.is_null() {
            // SAFETY: Frees buffer allocated by `SecKeychainFindGenericPassword`.
            unsafe {
                SecKeychainItemFreeContent(ptr::null(), data);
            }
        }
        result
    }

    pub(super) fn encryption_key() -> Option<[u8; 32]> {
        static KEY: OnceLock<Option<[u8; 32]>> = OnceLock::new();
        *KEY.get_or_init(|| {
            match find_key() {
                Ok(Some(key)) => return Some(key),
                Ok(None) => {}
                Err(status) => {
                    tracing::warn!("macOS Keychain lookup failed with status {status}");
                    return None;
                }
            }

            let mut key = [0u8; 32];
            rand::RngCore::fill_bytes(&mut rand::rngs::OsRng, &mut key);
            // SAFETY: Storing 32-byte key in default macOS login keychain.
            let status = unsafe {
                SecKeychainAddGenericPassword(
                    ptr::null(),
                    SERVICE.len() as u32,
                    SERVICE.as_ptr(),
                    ACCOUNT.len() as u32,
                    ACCOUNT.as_ptr(),
                    key.len() as u32,
                    key.as_ptr().cast(),
                    ptr::null_mut(),
                )
            };
            match status {
                ERR_SEC_SUCCESS => Some(key),
                ERR_SEC_DUPLICATE_ITEM => find_key().ok().flatten(),
                _ => {
                    tracing::warn!("macOS Keychain key creation failed with status {status}");
                    None
                }
            }
        })
    }
}

#[cfg(target_os = "macos")]
fn protect_platform(plaintext: &str) -> String {
    use aes_gcm::{Aes256Gcm, KeyInit, Nonce, aead::Aead};

    let Some(key) = macos_keychain::encryption_key() else {
        return plaintext.to_owned();
    };
    let cipher = Aes256Gcm::new_from_slice(&key).expect("32-byte AES-256 key");
    let mut nonce_bytes = [0u8; 12];
    rand::RngCore::fill_bytes(&mut rand::rngs::OsRng, &mut nonce_bytes);
    let nonce = Nonce::from_slice(&nonce_bytes);
    let Ok(ciphertext) = cipher.encrypt(nonce, plaintext.as_bytes()) else {
        tracing::warn!("AES-GCM token encryption failed");
        return plaintext.to_owned();
    };
    let mut payload = Vec::with_capacity(12 + ciphertext.len());
    payload.extend_from_slice(&nonce_bytes);
    payload.extend_from_slice(&ciphertext);
    format!("aesgcm:v1:{}", STANDARD.encode(payload))
}

#[cfg(target_os = "macos")]
fn unprotect_platform(stored: &str) -> String {
    use aes_gcm::{Aes256Gcm, KeyInit, Nonce, aead::Aead};

    let Some(b64) = stored.strip_prefix("aesgcm:v1:") else {
        return stored.to_owned();
    };
    let Some(key) = macos_keychain::encryption_key() else {
        return stored.to_owned();
    };
    let Ok(payload) = STANDARD.decode(b64) else {
        tracing::warn!("Failed to base64-decode AES-GCM token");
        return stored.to_owned();
    };
    if payload.len() < 12 {
        tracing::warn!("AES-GCM token payload too short");
        return stored.to_owned();
    }
    let (nonce_bytes, ciphertext) = payload.split_at(12);
    let cipher = Aes256Gcm::new_from_slice(&key).expect("32-byte AES-256 key");
    let Ok(plaintext) = cipher.decrypt(Nonce::from_slice(nonce_bytes), ciphertext) else {
        tracing::warn!("AES-GCM token decryption failed");
        return stored.to_owned();
    };
    String::from_utf8(plaintext).unwrap_or_else(|_| {
        tracing::warn!("AES-GCM decrypted token is not valid UTF-8");
        stored.to_owned()
    })
}

#[cfg(not(any(windows, target_os = "macos")))]
fn protect_platform(plaintext: &str) -> String {
    plaintext.to_owned()
}

#[cfg(not(any(windows, target_os = "macos")))]
fn unprotect_platform(stored: &str) -> String {
    stored.to_owned()
}

#[cfg(test)]
mod tests {
    use super::{protect_token, unprotect_token};

    #[test]
    fn sentinels_are_untouched() {
        for sentinel in ["", "0", "offline"] {
            assert_eq!(protect_token(sentinel), sentinel);
            assert_eq!(unprotect_token(sentinel), sentinel);
        }
    }

    #[test]
    fn legacy_plaintext_passes_through() {
        let legacy = "eyJhbGciOiJSUzI1NiJ9.legacy-token";
        assert_eq!(unprotect_token(legacy), legacy);
    }

    #[test]
    fn invalid_protected_payload_does_not_panic() {
        for bad in ["dpapi:v1:!!!not-base64!!!", "dpapi:v1:YWJj", "aesgcm:v1:!!!", "aesgcm:v1:YWJj"] {
            assert_eq!(unprotect_token(bad), bad);
        }
    }

    #[test]
    fn round_trip_protects_and_restores_token() {
        let secret = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.sample-minecraft-token";
        let protected = protect_token(secret);
        #[cfg(windows)]
        assert!(protected.starts_with("dpapi:v1:"));
        #[cfg(windows)]
        assert_ne!(protected, secret);
        assert_eq!(unprotect_token(&protected), secret);
    }
}
