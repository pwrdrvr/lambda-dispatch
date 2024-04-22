Ha ha, no, you didn't find anything interesting!

This cert is used only for unit tests. It's not a real cert, and it's not used in any real-world scenarios. It's just a placeholder for the unit tests to use. If you're looking for the real cert, you won't find it here. Sorry!

```sh
openssl req -x509 -newkey rsa:4096 -keyout key.pem -out cert.pem -days 365 -nodes -subj "/CN=localhost" -config <(cat <<-EOF
[req]
distinguished_name = req_distinguished_name
x509_extensions = v3_req
prompt = no
[req_distinguished_name]
CN = localhost
[v3_req]
subjectAltName = @alt_names
[alt_names]
DNS.1 = localhost
EOF
)
```