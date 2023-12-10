# Handle HTTP

- [ ] Router - Incoming - Read Request (writes response to Lambda, sending the request)
  - The request is already parsed by Kestrel
  - We just need to write out the headers, a blank line, then the body
  - We don't even have to figure out the chunk lengths as Kestrel is doing that for us
- [ ] Router - Callback - Read Request (receives response from Lambda, writes resposne to incoming)
  - The response is written via Kestrel
  - The chunk lengths are already parsed by Kestrel and removed from the body
  - We can write directly to the response as we read from the Callback Request
  - We can possibly use a CopyToAsync to write the body
  - The only tricky bit is not using StreamReader for the headers
  - This should look most like the ResponseParser in Kestrel
- [ ] Lambda - Incoming - Read Request
  - System.Net.Http.HttpRequestMessage can be used to represent the parsed request
  - We have to read the chunk sizes, read the headers in the body, then read the body while removing the chunk sizes from the body
  - The acutal request could be chunked too in which case there would be chunking within chunking which is fine
  - Could we use Kestrels classes to parse the request?
- [ ] Lambda - Proxy - Read Response
  - We have to write the chunk sizes, write the headers in the body, then write the body

# Kestrel C# Request/Response Parser

## Http1ParsingHandler

https://github.com/dotnet/aspnetcore/blob/52364da7f2d8e8956085a92c2f6b9dae48ac130d/src/Servers/Kestrel/Core/src/Internal/Http/Http1ParsingHandler.cs

## HttpParser

https://github.com/dotnet/aspnetcore/blob/52364da7f2d8e8956085a92c2f6b9dae48ac130d/src/Servers/Kestrel/Core/src/Internal/Http/HttpParser.cs

# AWS Docs

## Lambda Invoke Request

https://docs.aws.amazon.com/lambda/latest/dg/API_Invoke.html

## Lambda Runtime Endpoints and Requests

https://docs.aws.amazon.com/lambda/latest/dg/runtimes-api.html
