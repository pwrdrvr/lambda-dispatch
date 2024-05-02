use std::sync::Arc;

pub use anyhow::{Context, Error, Result};
pub type LambdaId = Arc<str>;
pub type PoolId = Arc<str>;
pub type ChannelId = Arc<str>;

pub trait Severity: Sized {
  /// Lower values represent higher severity with zero being the highest (akin to SEV0, SEV1, etc).
  fn severity(&self) -> usize;

  /// Compares two [`Severity`]s, returning the highest.
  fn worse(self, other: Self) -> Self {
    if self.severity() > other.severity() {
      return other;
    }
    self
  }
}
