use base64::{Engine, engine::general_purpose::STANDARD};
use image::{DynamicImage, ImageFormat, RgbaImage, imageops};
use serde_json::{Value, json};
use std::{
    fs::File,
    io::{Cursor, Read},
    path::Path,
};
use theseus::minecraft_skins::{self, MinecraftSkinVariant, UrlOrBlob};

pub(super) fn read_texture(path: &Path) -> super::Result<Vec<u8>> {
    const LIMIT: u64 = 1024 * 1024;
    let file = File::open(path)?;
    if !file.metadata()?.is_file() {
        return Err("Виберіть PNG-файл скіна.".into());
    }
    let mut bytes = Vec::new();
    file.take(LIMIT + 1).read_to_end(&mut bytes)?;
    if bytes.len() as u64 > LIMIT || !bytes.starts_with(b"\x89PNG\r\n\x1a\n") {
        return Err("Виберіть PNG-файл скіна розміром до 1 МіБ.".into());
    }
    Ok(bytes)
}

pub(super) async fn current_preview() -> super::Result<Value> {
    let equipped = Box::pin(minecraft_skins::get_available_skins())
        .await?
        .into_iter()
        .find(|skin| skin.is_equipped);
    let Some(equipped) = equipped else {
        return Ok(Value::Null);
    };
    let texture = UrlOrBlob::Url(equipped.texture.as_ref().clone());
    let png =
        Box::pin(minecraft_skins::normalize_skin_texture(&texture)).await?;
    preview(&png, matches!(equipped.variant, MinecraftSkinVariant::Slim))
}

pub(super) fn preview(bytes: &[u8], slim: bool) -> super::Result<Value> {
    if bytes.len() < 24
        || !bytes.starts_with(b"\x89PNG\r\n\x1a\n")
        || &bytes[12..16] != b"IHDR"
        || bytes[16..20] != 64u32.to_be_bytes()
        || !(bytes[20..24] == 32u32.to_be_bytes()
            || bytes[20..24] == 64u32.to_be_bytes())
    {
        return Err("Скін має бути PNG 64×64 або 64×32.".into());
    }
    let atlas = image::load_from_memory_with_format(bytes, ImageFormat::Png)?
        .into_rgba8();
    let modern = atlas.height() == 64;
    let arm = if slim && modern { 3 } else { 4 };
    let mut front = RgbaImage::new(16, 32);
    let mut draw = |source: (u32, u32),
                    size: (u32, u32),
                    target: (i64, i64),
                    flip: bool| {
        let mut part =
            imageops::crop_imm(&atlas, source.0, source.1, size.0, size.1)
                .to_image();
        if flip {
            imageops::flip_horizontal_in_place(&mut part);
        }
        imageops::overlay(&mut front, &part, target.0, target.1);
    };
    let parts = [
        ((8, 8), (40, 8), (8, 8), (4, 0)),
        ((20, 20), (20, 36), (8, 12), (4, 8)),
        ((44, 20), (44, 36), (arm, 12), (4 - i64::from(arm), 8)),
        ((36, 52), (52, 52), (arm, 12), (12, 8)),
        ((4, 20), (4, 36), (4, 12), (4, 20)),
        ((20, 52), (4, 52), (4, 12), (8, 20)),
    ];
    for (index, (base, overlay, size, target)) in parts.into_iter().enumerate()
    {
        if modern {
            draw(base, size, target, false);
            draw(overlay, size, target, false);
        } else {
            let mirror = index == 3 || index == 5;
            let source = if mirror { parts[index - 1].0 } else { base };
            draw(source, size, target, mirror);
        }
    }
    let mut png = Cursor::new(Vec::new());
    DynamicImage::ImageRgba8(imageops::resize(
        &front,
        128,
        256,
        imageops::FilterType::Nearest,
    ))
    .write_to(&mut png, ImageFormat::Png)?;
    Ok(
        json!({ "preview": STANDARD.encode(png.into_inner()), "variant": if slim { "slim" } else { "classic" } }),
    )
}
