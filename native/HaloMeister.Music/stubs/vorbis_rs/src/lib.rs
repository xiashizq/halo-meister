//! Stand-in for `vorbis_rs`. Playback decodes Wwise-Vorbis with `lewton`.

use std::fmt;
use std::num::{NonZeroU32, NonZeroU8};

#[derive(Debug)]
pub struct VorbisError;

impl fmt::Display for VorbisError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str("vorbis encoder is not linked in the music tool")
    }
}

impl std::error::Error for VorbisError {}

pub struct VorbisEncoderBuilder;

pub struct VorbisEncoder;

impl VorbisEncoderBuilder {
    pub fn new<W>(
        _sample_rate: NonZeroU32,
        _channels: NonZeroU8,
        _writer: W,
    ) -> Result<Self, VorbisError> {
        Err(VorbisError)
    }

    pub fn build(self) -> Result<VorbisEncoder, VorbisError> {
        Err(VorbisError)
    }
}

impl VorbisEncoder {
    pub fn encode_audio_block(&mut self, _block: &[&[f32]]) -> Result<(), VorbisError> {
        Err(VorbisError)
    }

    pub fn finish(self) -> Result<(), VorbisError> {
        Ok(())
    }
}
