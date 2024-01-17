use std::sync::atomic::{AtomicUsize, Ordering};

pub struct DecrementOnDrop<'a>(pub &'a AtomicUsize);

impl<'a> Drop for DecrementOnDrop<'a> {
  fn drop(&mut self) {
    self.0.fetch_sub(1, Ordering::SeqCst);
  }
}
