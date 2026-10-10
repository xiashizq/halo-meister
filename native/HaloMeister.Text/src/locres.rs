//! Unreal `Game.locres` reader.
//!
//! Layout follows `FTextLocalizationResource` (magic GUID, version byte, string
//! table, then namespace/key entries). CityHash64 files store an 8-byte key
//! hash; older optimized files store a 4-byte CRC.

use anyhow::{bail, Result};

const MAGIC: [u8; 16] = [
    0x0E, 0x14, 0x74, 0x75, 0x67, 0x4A, 0x03, 0xFC, 0x4A, 0x15, 0x90, 0x9D, 0xC3, 0x37, 0x7F, 0x1B,
];

#[derive(Clone, Debug)]
pub struct LocEntry {
    pub namespace: String,
    pub key: String,
    pub text: String,
}

pub fn parse(data: &[u8]) -> Result<Vec<LocEntry>> {
    if data.len() >= 17 && data[..16] == MAGIC {
        let version = data[16];
        if version > 3 {
            bail!("unsupported locres version {version}");
        }
        // Version 3 is CityHash64 (8-byte hashes). If a file was written with the
        // shorter hash, the first pass fails and the 4-byte layout is tried.
        let wide = version >= 3;
        match parse_body(data, 17, version, wide) {
            Ok(entries) => Ok(entries),
            Err(error) if wide => parse_body(data, 17, version, false).map_err(|_| error),
            Err(error) => Err(error),
        }
    } else {
        parse_body(data, 0, 0, false)
    }
}

fn parse_body(data: &[u8], mut pos: usize, version: u8, wide_hash: bool) -> Result<Vec<LocEntry>> {
    let mut strings = Vec::new();
    if version >= 1 {
        let offset = read_i64(data, &mut pos)?;
        if offset >= 0 {
            strings = read_string_table(data, offset as usize, version)?;
        }
    }
    if version >= 2 {
        let _entries = read_u32(data, &mut pos)?;
    }

    let namespace_count = read_u32(data, &mut pos)? as usize;
    let mut entries = Vec::new();
    for _ in 0..namespace_count {
        let namespace = read_key(data, &mut pos, version, wide_hash)?;
        let key_count = read_u32(data, &mut pos)? as usize;
        for _ in 0..key_count {
            let key = read_key(data, &mut pos, version, wide_hash)?;
            let _source_hash = read_u32(data, &mut pos)?;
            let text = if version >= 1 {
                let index = read_i32(data, &mut pos)?;
                if index < 0 {
                    String::new()
                } else {
                    strings
                        .get(index as usize)
                        .cloned()
                        .unwrap_or_default()
                }
            } else {
                read_fstring(data, &mut pos)?
            };
            if key.is_empty() && text.is_empty() {
                continue;
            }
            entries.push(LocEntry {
                namespace: namespace.clone(),
                key,
                text,
            });
        }
    }
    Ok(entries)
}

fn read_string_table(data: &[u8], mut pos: usize, version: u8) -> Result<Vec<String>> {
    let count = read_i32(data, &mut pos)?;
    if count < 0 {
        bail!("locres string table count is negative");
    }
    let mut strings = Vec::with_capacity(count as usize);
    for _ in 0..count {
        strings.push(read_fstring(data, &mut pos)?);
        if version >= 2 {
            let _refs = read_i32(data, &mut pos)?;
        }
    }
    Ok(strings)
}

fn read_key(data: &[u8], pos: &mut usize, version: u8, wide_hash: bool) -> Result<String> {
    if version >= 2 {
        *pos += if wide_hash { 8 } else { 4 };
        if *pos > data.len() {
            bail!("locres key hash runs past the end of the file");
        }
    }
    read_fstring(data, pos)
}

fn read_fstring(data: &[u8], pos: &mut usize) -> Result<String> {
    let len = read_i32(data, pos)?;
    if len == 0 {
        return Ok(String::new());
    }
    if len < 0 {
        let chars = (-len) as usize;
        let bytes = take(data, pos, chars.saturating_mul(2))?;
        let units: Vec<u16> = bytes
            .chunks_exact(2)
            .map(|chunk| u16::from_le_bytes([chunk[0], chunk[1]]))
            .collect();
        return Ok(String::from_utf16_lossy(&units)
            .trim_end_matches('\0')
            .to_string());
    }
    let bytes = take(data, pos, len as usize)?;
    Ok(String::from_utf8_lossy(bytes)
        .trim_end_matches('\0')
        .to_string())
}

fn take<'a>(data: &'a [u8], pos: &mut usize, len: usize) -> Result<&'a [u8]> {
    let end = pos.saturating_add(len);
    if end > data.len() {
        bail!("locres string runs past the end of the file");
    }
    let slice = &data[*pos..end];
    *pos = end;
    Ok(slice)
}

fn read_u32(data: &[u8], pos: &mut usize) -> Result<u32> {
    let bytes = take(data, pos, 4)?;
    Ok(u32::from_le_bytes(bytes.try_into().unwrap()))
}

fn read_i32(data: &[u8], pos: &mut usize) -> Result<i32> {
    Ok(read_u32(data, pos)? as i32)
}

fn read_i64(data: &[u8], pos: &mut usize) -> Result<i64> {
    let bytes = take(data, pos, 8)?;
    Ok(i64::from_le_bytes(bytes.try_into().unwrap()))
}
