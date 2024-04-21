use std::sync::atomic::{AtomicUsize, Ordering};

pub struct DecrementOnDrop<'a>(pub &'a AtomicUsize);

impl<'a> Drop for DecrementOnDrop<'a> {
  fn drop(&mut self) {
    self.0.fetch_sub(1, Ordering::SeqCst);
  }
}

#[cfg(test)]
mod tests {
  use super::*;
  use std::sync::Arc;
  use tokio::time::{sleep, Duration};

  #[tokio::test]
  async fn test_decrement_on_drop() {
    let counter = Arc::new(AtomicUsize::new(1));

    {
      let _decrement_on_drop = DecrementOnDrop(&counter);
    } // _decrement_on_drop goes out of scope here and should be dropped

    sleep(Duration::from_millis(100)).await; // give some time for the drop handler to run

    assert_eq!(counter.load(Ordering::SeqCst), 0);
  }
}
