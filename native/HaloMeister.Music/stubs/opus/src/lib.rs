//! Stand-in for the `opus` crate. Halo 2 Opus is unused by the music tool.

use std::fmt;

#[derive(Debug)]
pub struct Error;

impl fmt::Display for Error {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str("opus is not linked in the music tool")
    }
}

impl std::error::Error for Error {}

#[derive(Clone, Copy)]
pub enum Channels {
    Mono = 1,
    Stereo = 2,
}

#[derive(Clone, Copy)]
pub enum Application {
    Audio,
    Voip,
    LowDelay,
}

pub struct Decoder;

impl Decoder {
    pub fn new(_sample_rate: u32, _channels: Channels) -> Result<Self, Error> {
        Err(Error)
    }

    pub fn decode(
        &mut self,
        _packet: &[u8],
        _output: &mut [i16],
        _fec: bool,
    ) -> Result<usize, Error> {
        Err(Error)
    }
}

pub struct Encoder;

impl Encoder {
    pub fn new(
        _sample_rate: u32,
        _channels: Channels,
        _application: Application,
    ) -> Result<Self, Error> {
        Err(Error)
    }

    pub fn encode(&mut self, _input: &[i16], _output: &mut [u8]) -> Result<usize, Error> {
        Err(Error)
    }
}
