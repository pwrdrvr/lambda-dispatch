use std::{
  fmt,
  sync::{atomic::AtomicUsize, Arc},
};
use axum::{
  extract::Path,
  response::Response,
  routing::{get, post},
  Router,
};
use axum_extra::body::AsyncReadBody;
use futures::stream::StreamExt;
use hyper::StatusCode;
use tokio::io::AsyncWriteExt;

use crate::support::http2_server::{run_http2_app, run_http2_tls_app, Serve};

#[allow(dead_code)]
#[derive(Clone, Copy, PartialEq, Debug)]
pub enum RequestMethod {
  Get,
  GetDoubleSlashPath,
  GetNoHost,
  GetPanic,
  GetQuerySimple,
  GetQueryEncoded,
  GetQueryUnencodedBrackets,
  GetQueryRepeated,
  ShutdownWithoutResponse,
  PostSimple,
  PostEcho,
  GetGoAwayOnBody,
  GetInvalidHeaders,
  GetEnormousHeaders,
  GetOversizedHeader,
}

#[allow(dead_code)]
#[derive(Clone, Copy, PartialEq)]
pub enum ListenerType {
  Http,
  Https,
}

impl fmt::Display for ListenerType {
  fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
    match *self {
      ListenerType::Http => write!(f, "http"),
      ListenerType::Https => write!(f, "https"),
    }
  }
}

#[derive(Clone, Copy)]
pub struct RouterParams {
  pub channel_return_request_without_wait_before_count: isize,
  pub channel_non_200_status_after_count: isize,
  pub channel_non_200_status_code: StatusCode,
  pub channel_panic_response_from_extension_on_count: isize,
  pub channel_panic_request_to_extension_before_start_on_count: isize,
  pub channel_panic_request_to_extension_after_start: bool,
  pub channel_panic_request_to_extension_before_close: bool,
  pub ping_panic_after_count: isize,
  pub request_method: RequestMethod,
  pub request_method_switch_to: RequestMethod,
  pub request_method_switch_after_count: isize,
  pub listener_type: ListenerType,
}

#[allow(dead_code)]
pub struct RouterResult {
  pub release_request_tx: Arc<tokio::sync::Mutex<tokio::sync::mpsc::Sender<()>>>,
  pub request_count: Arc<AtomicUsize>,
  pub ping_count: Arc<AtomicUsize>,
  pub close_count: Arc<AtomicUsize>,
  pub server: Serve,
}

#[allow(dead_code)]
pub struct RouterParamsBuilder {
    params: RouterParams,
}

#[allow(dead_code)]
impl RouterParamsBuilder {
    pub fn new() -> RouterParamsBuilder {
        RouterParamsBuilder {
            params: RouterParams {
                request_method: RequestMethod::Get,
                request_method_switch_to: RequestMethod::Get,
                request_method_switch_after_count: -1,
                channel_return_request_without_wait_before_count: -1,
                channel_non_200_status_after_count: -1,
                channel_non_200_status_code: StatusCode::CONFLICT,
                channel_panic_response_from_extension_on_count: -1,
                channel_panic_request_to_extension_before_start_on_count: -1,
                channel_panic_request_to_extension_after_start: false,
                channel_panic_request_to_extension_before_close: false,
                ping_panic_after_count: -1,
                listener_type: ListenerType::Http,
            },
        }
    }

    pub fn request_method(mut self, request_method: RequestMethod) -> Self {
        self.params.request_method = request_method;
        self
    }

    pub fn request_method_switch_to(mut self, request_method: RequestMethod) -> Self {
        self.params.request_method_switch_to = request_method;
        self
    }

    pub fn request_method_switch_after_count(mut self, count: isize) -> Self {
        self.params.request_method_switch_after_count = count;
        self
    }

    pub fn channel_return_request_without_wait_before_count(mut self, count: isize) -> Self {
        self.params.channel_return_request_without_wait_before_count = count;
        self
    }

    pub fn channel_non_200_status_after_count(mut self, count: isize) -> Self {
        self.params.channel_non_200_status_after_count = count;
        self
    }

    pub fn channel_non_200_status_code(mut self, status_code: StatusCode) -> Self {
        self.params.channel_non_200_status_code = status_code;
        self
    }

    pub fn channel_panic_response_from_extension_on_count(mut self, count: isize) -> Self {
        self.params.channel_panic_response_from_extension_on_count = count;
        self
    }

    pub fn channel_panic_request_to_extension_before_start_on_count(mut self, count: isize) -> Self {
        self.params.channel_panic_request_to_extension_before_start_on_count = count;
        self
    }

    pub fn channel_panic_request_to_extension_after_start(mut self, panic: bool) -> Self {
        self.params.channel_panic_request_to_extension_after_start = panic;
        self
    }

    pub fn channel_panic_request_to_extension_before_close(mut self, panic: bool) -> Self {
        self.params.channel_panic_request_to_extension_before_close = panic;
        self
    }

    pub fn ping_panic_after_count(mut self, count: isize) -> Self {
        self.params.ping_panic_after_count = count;
        self
    }

    pub fn listener_type(mut self, listener_type: ListenerType) -> Self {
        self.params.listener_type = listener_type;
        self
    }

    pub fn build(self) -> RouterParams {
        self.params
    }
}

#[allow(dead_code)]
pub fn setup_router(params: RouterParams) -> RouterResult {
  // Start router server
  let (release_request_tx, release_request_rx) = tokio::sync::mpsc::channel::<()>(100);
  let release_request_rx = Arc::new(tokio::sync::Mutex::new(release_request_rx));
  let release_request_tx = Arc::new(tokio::sync::Mutex::new(release_request_tx));
  let release_request_tx_close = Arc::clone(&release_request_tx);
  // Use an arc int to count how many times the request endpoint was called
  let request_count: Arc<AtomicUsize> = Arc::new(std::sync::atomic::AtomicUsize::new(0));
  let request_count_clone = Arc::clone(&request_count);
  let ping_count = Arc::new(std::sync::atomic::AtomicUsize::new(0));
  let ping_count_clone = Arc::clone(&ping_count);
  let close_count = Arc::new(std::sync::atomic::AtomicUsize::new(0));
  let close_count_clone = Arc::clone(&close_count);
  let app = Router::new()
        .route(
            "/api/chunked/request/:lambda_id/:channel_id",
            post(
                move |Path((_lambda_id, _channel_id)): Path<(String, String)>,
                request: axum::extract::Request| {
                    let request_count = Arc::clone(&request_count_clone);
                    let release_request_rx = Arc::clone(&release_request_rx);

                    async move {
                        // Increment the request count
                        let request_count = 1 + request_count.fetch_add(1, std::sync::atomic::Ordering::SeqCst);

                        // Bail after request count if desired
                        if params.channel_non_200_status_after_count >= 0 && request_count > params.channel_non_200_status_after_count as usize {
                            let body = AsyncReadBody::new(tokio::io::empty());
                            let response = Response::builder()
                                .status(params.channel_non_200_status_code)
                                .body(body)
                                .unwrap();

                            return response;
                        }

                        // Spawn a task to read from the request (receiving response from extension)
                        // We do not do anything with the response other than log it
                        // This would normally go back to the client of the router
                        tokio::spawn(async move {
                            let parts = request.into_parts();

                            if params.channel_panic_response_from_extension_on_count == 0
                            || request_count == params.channel_panic_response_from_extension_on_count as usize {
                                panic!("Panic! Response from extension");
                            }

                            if params.request_method == RequestMethod::PostEcho
                            && params.channel_panic_request_to_extension_before_start_on_count >= 0
                            && request_count > params.channel_panic_request_to_extension_before_start_on_count as usize {
                                // Read the bytes
                                // Decode the gzip
                                // Confirm we got 10 KB of 'a'
                                let buf = parts.1.into_data_stream().fold(Vec::new(), |mut buf, chunk| async move {
                                    let chunk = chunk.unwrap();
                                    buf.extend_from_slice(&chunk);
                                    buf
                                }).await;
                                assert_eq!(String::from_utf8(buf).unwrap(), "a".repeat(10 * 1024));
                            } else if params.request_method == RequestMethod::GetEnormousHeaders {
                                // Read the bytes
                                let buf = parts.1.into_data_stream().fold(Vec::new(), |mut buf, chunk| async move {
                                    let chunk = chunk.unwrap();
                                    buf.extend_from_slice(&chunk);
                                    buf
                                }).await;
                                let body_str = String::from_utf8(buf).unwrap();

                                let value = "a".repeat(1024 * 31);
                                assert!(body_str.contains(format!("test-header-0: {}", value).as_str()));
                                assert!(body_str.contains(format!("test-header-1: {}", value).as_str()));
                                assert!(body_str.contains(format!("test-header-2: {}", value).as_str()));
                                assert!(body_str.contains(format!("test-header-3: {}", value).as_str()));
                            } else if params.request_method == RequestMethod::GetOversizedHeader {
                                // Read the bytes
                                let buf = parts.1.into_data_stream().fold(Vec::new(), |mut buf, chunk| async move {
                                    let chunk = chunk.unwrap();
                                    buf.extend_from_slice(&chunk);
                                    buf
                                }).await;
                                let buf_len = buf.len();
                                let body_str = String::from_utf8(buf).unwrap();

                                let value = "a".repeat(1024 * 64);
                                assert!(buf_len > 64 * 1024, "Body length should have been > 64 KB: {}", buf_len);
                                assert!(body_str.contains(format!("test-header: {}", value).as_str()));
                            } else {
                                // Read and discard the response body chunks
                                parts
                                    .1
                                    .into_data_stream()
                                    .for_each(|chunk| async {
                                        let _chunk = chunk.unwrap();
                                        // println!("Chunk: {:?}", _chunk);
                                    })
                                .await;
                            }
                        });

                        // Create a channel for the stream
                        let (mut tx, rx) = tokio::io::duplex(65_536);

                        // Spawn a task to write to the response (sending request to extension)
                        tokio::spawn(async move {
                            if params.channel_panic_response_from_extension_on_count == 0
                            || request_count == params.channel_panic_response_from_extension_on_count as usize {
                                panic!("Panic! Response from extension");
                            }

                            let request_method = if params.request_method_switch_after_count >= 0
                            && request_count > params.request_method_switch_after_count as usize {
                                println!("{} Switching to: {:?}, request_count: {}", chrono::Local::now().format("%Y-%m-%d %H:%M:%S%.3f"), params.request_method_switch_to, request_count);
                                params.request_method_switch_to
                            } else {
                                println!("{} Using: {:?}, request_count: {}", chrono::Local::now().format("%Y-%m-%d %H:%M:%S%.3f"), params.request_method, request_count);
                                params.request_method
                            };

                            // Wait for the release before indicating that the body is finished
                            // Keep in mind that this is HTTP/1.1 WITHOUT CHUNKING and WITHOUT CONTENT-LENGTH header
                            // We are sending this over HTTP2 so closing the stream is the only way to indicate the end of the body,
                            // similar to Transfer-Encoding: chunked

                            // Wait for the release before sending the request to the extension
                            // Note that our headers reader will read the headers if we send them
                            // so we cannot send the headers until we want the extension to process
                            // the request
                            if params.channel_return_request_without_wait_before_count < 0
                            || request_count >= params.channel_return_request_without_wait_before_count as usize {
                                match release_request_rx.lock().await.recv().await {
                                    None => {
                                        // The channel was closed
                                        println!("{} Channel closed, request_count: {}", chrono::Local::now().format("%Y-%m-%d %H:%M:%S%.3f"), request_count);
                                    }
                                    Some(_) => {
                                        println!("{} Channel released, request_count: {}", chrono::Local::now().format("%Y-%m-%d %H:%M:%S%.3f"), request_count);
                                    }
                                }
                            } else {
                                println!("{} Channel released before count: {}, request_count: {}", chrono::Local::now().format("%Y-%m-%d %H:%M:%S%.3f"), params.channel_return_request_without_wait_before_count, request_count);
                            }

                            // Send static request to extension
                            if request_method == RequestMethod::ShutdownWithoutResponse {
                              // Close the body stream
                              tx.shutdown().await.unwrap();
                              return;
                            } else if request_method == RequestMethod::PostSimple {
                                let data = b"POST /bananas HTTP/1.1\r\nHost: localhost\r\nTest-Header: foo\r\n\r\nHELLO WORLD";
                                tx.write_all(data).await.unwrap();
                            } else if request_method == RequestMethod::PostEcho {
                                let data = b"POST /bananas_echo HTTP/1.1\r\nHost: localhost\r\nAccept-Encoding: gzip\r\nTest-Header: foo\r\n\r\n";
                                tx.write_all(data).await.unwrap();

                                let data = "a".repeat(10 * 1024);
                                tx.write_all(data.as_bytes()).await.unwrap();
                            } else if request_method == RequestMethod::GetDoubleSlashPath {
                                let data = b"GET // HTTP/1.1\r\nHost: localhost\r\nTest-Header: foo\r\n\r\n";
                                tx.write_all(data).await.unwrap(); 
                            } else if request_method == RequestMethod::GetQuerySimple {
                                let data = b"GET /bananas_query_simple?cat=dog&frog=log HTTP/1.1\r\nHost: localhost\r\nTest-Header: foo\r\n\r\n";
                                tx.write_all(data).await.unwrap();
                            } else if request_method == RequestMethod::GetQueryRepeated {
                                let data = b"GET /bananas_query_repeated?cat=dog&cat=log&cat=cat HTTP/1.1\r\nHost: localhost\r\nTest-Header: foo\r\n\r\n";
                                tx.write_all(data).await.unwrap();
                            } else if request_method == RequestMethod::GetQueryEncoded {
                                let data = b"GET /bananas_query_encoded?cat=dog%25&cat=%22log%22&cat=cat HTTP/1.1\r\nHost: localhost\r\nTest-Header: foo\r\n\r\n";
                                tx.write_all(data).await.unwrap();
                            } else if request_method == RequestMethod::GetQueryUnencodedBrackets {
                                let data = b"GET /bananas_query_unencoded_brackets?cat=[dog]&cat=log&cat=cat HTTP/1.1\r\nHost: localhost\r\nTest-Header: foo\r\n\r\n";
                                tx.write_all(data).await.unwrap();
                            } else if request_method == RequestMethod::GetGoAwayOnBody {
                                println!("{} Sending GoAway on body, request_count: {}", chrono::Local::now().format("%Y-%m-%d %H:%M:%S%.3f"), request_count);
                                let data = b"GET /_lambda_dispatch/goaway HTTP/1.1\r\nHost: localhost\r\nTest-Header: foo\r\n\r\n";
                                tx.write_all(data).await.unwrap();
                            } else if request_method == RequestMethod::GetInvalidHeaders {
                                let data = b"GET /bananas/invalid_headers HTTP/1.1\r\nHost: localhost\r\nTest-Header: foo\r\n:\r\n\r\n";
                                tx.write_all(data).await.unwrap();
                            } else if request_method == RequestMethod::GetEnormousHeaders {
                                let data = b"GET /bananas/enormous_headers HTTP/1.1\r\nHost: localhost\r\n";
                                tx.write_all(data).await.unwrap();

                                let header_value = "a".repeat(1024 * 31); // Each header value is 31 KB
                                for i in 0..4 {
                                    let header = format!("Test-Header-{}: {}\r\n", i, header_value);
                                    tx.write_all(header.as_bytes()).await.unwrap();
                                }

                                let data = b"\r\n";
                                tx.write_all(data).await.unwrap();
                            } else if request_method == RequestMethod::GetOversizedHeader {
                                let data = b"GET /bananas/oversized_header HTTP/1.1\r\nHost: localhost\r\n";
                                tx.write_all(data).await.unwrap();

                                let data = b"Test-Header: ";
                                tx.write_all(data).await.unwrap();

                                let data = "a".repeat(64 * 1024);
                                tx.write_all(data.as_bytes()).await.unwrap();

                                let data = b"\r\n\r\n";
                                tx.write_all(data).await.unwrap();
                            } else if request_method == RequestMethod::GetNoHost {
                                let data = b"GET /bananas/no_host_header HTTP/1.1\r\nTest-Header: foo\r\n\r\n";
                                tx.write_all(data).await.unwrap();
                            } else if request_method == RequestMethod::GetPanic {
                                panic!("Panic! Request to extension");
                            } else {
                                let data = b"GET /bananas HTTP/1.1\r\nHost: localhost\r\nTest-Header: foo\r\n";
                                tx.write_all(data).await.unwrap();

                                if params.channel_panic_request_to_extension_after_start {
                                    panic!("Panic! Request to extension, after sending VERB line and some headers");
                                }

                                // Write the rest of the headers
                                let data = b"Test-Headers: bar\r\nTest-Headerss: baz\r\nAccept-Encoding: gzip\r\n\r\nHELLO WORLD";
                                tx.write_all(data).await.unwrap();
                            }

                            if params.channel_panic_request_to_extension_before_close {
                                panic!("Panic! Request to extension, before close");
                            }

                            // Close the body stream
                            tx.shutdown().await.unwrap();
                        });

                        let body = AsyncReadBody::new(rx);

                        let response = Response::builder()
                            .header("content-type", "application/octet-stream")
                            .body(body)
                            .unwrap();

                        response
                    }
                }),
        )
        .route(
            "/api/chunked/ping/:lambda_id",
            get(move |Path(lambda_id): Path<String>|
                async move {
                    let ping_count = Arc::clone(&ping_count_clone);
                    // Increment
                    ping_count.fetch_add(1, std::sync::atomic::Ordering::SeqCst);

                    // Panic so the stream closes
                    if params.ping_panic_after_count >= 0 && ping_count.load(std::sync::atomic::Ordering::SeqCst) > params.ping_panic_after_count as usize {
                        panic!("Ping! LambdaID: {}", lambda_id);
                    }

                    log::info!("Ping! LambdaID: {}", lambda_id);
                    format!("Ping! LambdaID: {}", lambda_id)
                }),
        )
        .route(
            "/api/chunked/close/:lambda_id",
            get(move |Path(lambda_id): Path<String>| async move {
                let close_count = Arc::clone(&close_count_clone);
                
                // Release any waiting requests
                for _ in 0..10 {
                    release_request_tx_close.lock().await.send(()).await.unwrap();
                }

                // Increment
                close_count.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
                println!("{} Close! LambdaID: {}", chrono::Local::now().format("%Y-%m-%d %H:%M:%S%.3f"), lambda_id);
                format!("Close! LambdaID: {}", lambda_id)
            }),
        );

  let mock_router_server = match params.listener_type {
    ListenerType::Http => run_http2_app(app),
    ListenerType::Https => run_http2_tls_app(app),
  };

  RouterResult {
    release_request_tx,
    request_count,
    ping_count,
    close_count,
    server: mock_router_server,
  }
}
