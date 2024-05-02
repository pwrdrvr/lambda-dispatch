#[cfg(test)]
mod tests {
  use extension::{options::Options, startup};
  use httpmock::{Method::GET, MockServer};
  use lazy_static::lazy_static;
  use std::sync::{Arc, Mutex};
  use tokio::runtime::Runtime;
  use tokio_test::assert_ok;

  lazy_static! {
    static ref TEST_MUTEX: Arc<Mutex<()>> = Arc::new(Mutex::new(()));
  }

  #[test]
  fn test_startup() {
    let _guard = TEST_MUTEX.lock().unwrap();
    // Start app server
    let app_server = MockServer::start();
    let app_healthcheck_mock = app_server.mock(|when, then| {
      when.method(GET).path("/health");
      then.status(200).body("OK");
    });

    // Set the environment variable
    std::env::set_var("AWS_LAMBDA_RUNTIME_API", "localhost:9001");
    std::env::set_var("AWS_LAMBDA_FUNCTION_NAME", "test_function");
    std::env::set_var("AWS_LAMBDA_FUNCTION_MEMORY_SIZE", "128");
    std::env::set_var("AWS_LAMBDA_FUNCTION_VERSION", "$LATEST");
    std::env::set_var(
      "LAMBDA_DISPATCH_PORT",
      app_server.address().port().to_string(),
    );

    // Run the async_main function
    let result = startup::startup();

    // Assert that the function returned successfully
    assert_ok!(result);
    // Assert app server's healthcheck endpoint got called
    app_healthcheck_mock.assert();
  }

  #[test]
  fn test_startup_default_multi_thread() {
    let _guard = TEST_MUTEX.lock().unwrap();
    // Start app server
    let app_server = MockServer::start();
    let app_healthcheck_mock = app_server.mock(|when, then| {
      when.method(GET).path("/health");
      then.status(200).body("OK");
    });

    // Set the environment variable
    std::env::set_var("AWS_LAMBDA_RUNTIME_API", "localhost:9001");
    std::env::set_var("AWS_LAMBDA_FUNCTION_NAME", "test_function");
    std::env::set_var("AWS_LAMBDA_FUNCTION_MEMORY_SIZE", "128");
    std::env::set_var("AWS_LAMBDA_FUNCTION_VERSION", "$LATEST");
    std::env::set_var(
      "LAMBDA_DISPATCH_PORT",
      app_server.address().port().to_string(),
    );
    std::env::set_var("LAMBDA_DISPATCH_RUNTIME", "default_multi_thread");

    // Run the async_main function
    let result = startup::startup();

    // Assert that the function returned successfully
    assert_ok!(result);
    // Assert app server's healthcheck endpoint got called
    app_healthcheck_mock.assert();
  }

  #[test]
  fn test_startup_multi_thread() {
    let _guard = TEST_MUTEX.lock().unwrap();
    // Start app server
    let app_server = MockServer::start();
    let app_healthcheck_mock = app_server.mock(|when, then| {
      when.method(GET).path("/health");
      then.status(200).body("OK");
    });

    // Set the environment variable
    std::env::set_var("AWS_LAMBDA_RUNTIME_API", "localhost:9001");
    std::env::set_var("AWS_LAMBDA_FUNCTION_NAME", "test_function");
    std::env::set_var("AWS_LAMBDA_FUNCTION_MEMORY_SIZE", "128");
    std::env::set_var("AWS_LAMBDA_FUNCTION_VERSION", "$LATEST");
    std::env::set_var(
      "LAMBDA_DISPATCH_PORT",
      app_server.address().port().to_string(),
    );
    std::env::set_var("LAMBDA_DISPATCH_RUNTIME", "multi_thread");

    // Run the async_main function
    let result = startup::startup();

    // Assert that the function returned successfully
    assert_ok!(result);
    // Assert app server's healthcheck endpoint got called
    app_healthcheck_mock.assert();
  }

  #[test]
  fn test_startup_multi_thread_tokio_worker_threads() {
    let _guard = TEST_MUTEX.lock().unwrap();
    // Start app server
    let app_server = MockServer::start();
    let app_healthcheck_mock = app_server.mock(|when, then| {
      when.method(GET).path("/health");
      then.status(200).body("OK");
    });

    // Set the environment variable
    std::env::set_var("AWS_LAMBDA_RUNTIME_API", "localhost:9001");
    std::env::set_var("AWS_LAMBDA_FUNCTION_NAME", "test_function");
    std::env::set_var("AWS_LAMBDA_FUNCTION_MEMORY_SIZE", "128");
    std::env::set_var("AWS_LAMBDA_FUNCTION_VERSION", "$LATEST");
    std::env::set_var(
      "LAMBDA_DISPATCH_PORT",
      app_server.address().port().to_string(),
    );
    std::env::set_var("LAMBDA_DISPATCH_RUNTIME", "multi_thread");
    std::env::set_var("TOKIO_WORKER_THREADS", "4");

    // Run the async_main function
    let result = startup::startup();

    // Assert that the function returned successfully
    assert_ok!(result);
    // Assert app server's healthcheck endpoint got called
    app_healthcheck_mock.assert();
  }

  #[test]
  fn test_async_main() {
    // Start app server
    let app_server = MockServer::start();
    let app_healthcheck_mock = app_server.mock(|when, then| {
      when.method(GET).path("/health");
      then.status(200).body("OK");
    });

    // Set the environment variable
    std::env::set_var("AWS_LAMBDA_RUNTIME_API", "localhost:9001");
    std::env::set_var("AWS_LAMBDA_FUNCTION_NAME", "test_function");
    std::env::set_var("AWS_LAMBDA_FUNCTION_MEMORY_SIZE", "128");
    std::env::set_var("AWS_LAMBDA_FUNCTION_VERSION", "$LATEST");

    let rt = Runtime::new().unwrap();
    let mut options = Options::default();
    options.async_init = false;
    options.compression = false;
    options.port = app_server.address().port();

    // Run the async_main function
    let result = rt.block_on(startup::async_main(options));

    // Assert that the function returned successfully
    assert_ok!(result);
    // Assert app server's healthcheck endpoint got called
    app_healthcheck_mock.assert();
  }

  #[test]
  fn test_async_main_async_init() {
    // Start app server
    let app_server = MockServer::start();
    let app_healthcheck_mock = app_server.mock(|when, then| {
      when.method(GET).path("/health");
      then.status(200).body("OK");
    });

    // Set the environment variable
    std::env::set_var("AWS_LAMBDA_RUNTIME_API", "localhost:9001");
    std::env::set_var("AWS_LAMBDA_FUNCTION_NAME", "test_function");
    std::env::set_var("AWS_LAMBDA_FUNCTION_MEMORY_SIZE", "128");
    std::env::set_var("AWS_LAMBDA_FUNCTION_VERSION", "$LATEST");

    let rt = Runtime::new().unwrap();
    let mut options = Options::default();
    options.async_init = true;
    options.compression = false;
    options.port = app_server.address().port();

    // Run the async_main function
    let result = rt.block_on(startup::async_main(options));

    // Assert that the function returned successfully
    assert_ok!(result);
    // Assert app server's healthcheck endpoint got called
    app_healthcheck_mock.assert();
  }
}
