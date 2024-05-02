#[cfg(test)]
mod tests {
  use extension::counter_drop::DecrementOnDrop;
  use std::sync::atomic::{AtomicUsize, Ordering};
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
