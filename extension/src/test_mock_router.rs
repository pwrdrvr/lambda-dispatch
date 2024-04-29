#[cfg(test)]
pub mod test_mock_router {
  use std::sync::{atomic::AtomicUsize, Arc};

  use crate::test_http2_server::test_http2_server::{run_http2_app, Serve};
  use axum::{
    extract::Path,
    response::Response,
    routing::{get, post},
    Router,
  };
  use axum_extra::body::AsyncReadBody;
  use futures::stream::StreamExt;
  use hyper::StatusCode;
  use tokio::{io::AsyncWriteExt, sync::mpsc::Sender};

  #[derive(Clone, Copy, PartialEq)]
  pub enum RequestMethod {
    Get,
    GetQuerySimple,
    GetQueryEncoded,
    GetQueryUnencodedBrackets,
    GetQueryRepeated,
    PostSimple,
    PostEcho
  }

  #[derive(Clone, Copy)]
  pub struct RouterParams {
    pub channel_non_200_status_after_count: isize,
    pub channel_non_200_status_code: StatusCode,
    pub channel_panic_response_from_extension_on_count: isize,
    pub channel_panic_request_to_extension_before_start_on_count: isize,
    pub channel_panic_request_to_extension_after_start: bool,
    pub channel_panic_request_to_extension_before_close: bool,
    pub ping_panic_after_count: isize,
    pub request_method: RequestMethod,
  }

  pub struct RouterResult {
    pub release_request_tx: Sender<()>,
    pub request_count: Arc<AtomicUsize>,
    pub ping_count: Arc<AtomicUsize>,
    pub close_count: Arc<AtomicUsize>,
    pub mock_router_server: Serve,
  }

  pub fn setup_router(params: RouterParams) -> RouterResult {
    // Start router server
    let (release_request_tx, release_request_rx) = tokio::sync::mpsc::channel::<()>(1);
    let release_request_rx = Arc::new(tokio::sync::Mutex::new(release_request_rx));
    // Use an arc int to count how many times the request endpoint was called
    let request_count = Arc::new(std::sync::atomic::AtomicUsize::new(0));
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
          request_count.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
          let request_count = request_count.load(std::sync::atomic::Ordering::SeqCst);

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
            } else {
              // Read and discard the response body chunks
              parts
                .1
                .into_data_stream()
                .for_each(|chunk| async {
                  let _chunk = chunk.unwrap();
                  // println!("Chunk: {:?}", chunk);
                })
                .await;
            }
          });

          // Bail after request count if desired

          if params.channel_non_200_status_after_count >= 0 && request_count > params.channel_non_200_status_after_count as usize {
            let body = AsyncReadBody::new(tokio::io::empty());
            let response = Response::builder()
              .status(params.channel_non_200_status_code)
              .body(body)
              .unwrap();

            return response;
          }

          // Create a channel for the stream
          let (mut tx, rx) = tokio::io::duplex(65_536);

          // Spawn a task to write to the response (sending request to extension)
          tokio::spawn(async move {
            if params.channel_panic_response_from_extension_on_count == 0
              || request_count == params.channel_panic_response_from_extension_on_count as usize {
              panic!("Panic! Response from extension");
            }

            // Send static request to extension
            if params.request_method == RequestMethod::PostSimple {
              let data = b"POST /bananas HTTP/1.1\r\nHost: localhost\r\nTest-Header: foo\r\n\r\nHELLO WORLD";
              tx.write_all(data).await.unwrap();
            } else if params.request_method == RequestMethod::PostEcho {
              let data = b"POST /bananas_echo HTTP/1.1\r\nHost: localhost\r\nAccept-Encoding: gzip\r\nTest-Header: foo\r\n\r\n";
              tx.write_all(data).await.unwrap();

              let data = "a".repeat(10 * 1024);
              tx.write_all(data.as_bytes()).await.unwrap();
            } else if params.request_method == RequestMethod::GetQuerySimple {
              let data = b"GET /bananas_query_simple?cat=dog&frog=log HTTP/1.1\r\nHost: localhost\r\nTest-Header: foo\r\n\r\n";
              tx.write_all(data).await.unwrap();
            } else if params.request_method == RequestMethod::GetQueryRepeated {
              let data = b"GET /bananas_query_repeated?cat=dog&cat=log&cat=cat HTTP/1.1\r\nHost: localhost\r\nTest-Header: foo\r\n\r\n";
              tx.write_all(data).await.unwrap();
            } else if params.request_method == RequestMethod::GetQueryEncoded {
              let data = b"GET /bananas_query_encoded?cat=dog%25&cat=%22log%22&cat=cat HTTP/1.1\r\nHost: localhost\r\nTest-Header: foo\r\n\r\n";
              tx.write_all(data).await.unwrap();
            } else if params.request_method == RequestMethod::GetQueryUnencodedBrackets {
              let data = b"GET /bananas_query_unencoded_brackets?cat=[dog]&cat=log&cat=cat HTTP/1.1\r\nHost: localhost\r\nTest-Header: foo\r\n\r\n";
              tx.write_all(data).await.unwrap();
            } else {
              let data = b"GET /bananas HTTP/1.1\r\nHost: localhost\r\nTest-Header: foo\r\n\r\n";
              tx.write_all(data).await.unwrap();

              if params.channel_panic_request_to_extension_after_start {
                panic!("Panic! Request to extension, after sending VERB line and some headers");
              }
  
              // Write the rest of the headers
              let data = b"Test-Headers: bar\r\nTest-Headerss: baz\r\nAccept-Encoding: gzip\r\n\r\nHELLO WORLD";
              tx.write_all(data).await.unwrap();
            }

            // Wait for the release before indicating that the body is finished
            // Keep in mind that this is HTTP/1.1 WITHOUT CHUNKING and WITHOUT CONTENT-LENGTH header
            // We are sending this over HTTP2 so closing the stream is the only way to indicate the end of the body,
            // similar to Transfer-Encoding: chunked
            release_request_rx.lock().await.recv().await;

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
        // Increment
        close_count.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
        log::info!("Close! LambdaID: {}", lambda_id);
        format!("Close! LambdaID: {}", lambda_id)
      }),
    );

    let mock_router_server = run_http2_app(app);

    RouterResult {
      release_request_tx,
      request_count,
      ping_count,
      close_count,
      mock_router_server,
    }
  }
}
