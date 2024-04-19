use std::sync::Arc;

pub use anyhow::{Context, Error, Result};
pub type LambdaId = Arc<str>;
pub type PoolId = Arc<str>;
pub type ChannelId = Arc<str>;
