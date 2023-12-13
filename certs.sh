#!/bin/bash

# Define variables
ROOT_CERT="Pwrdrvr.LambdaDispatch"
DOMAIN="lambdadispatch.local"
ROOT_CERT_EXPIRY=365
DOMAIN_CERT_EXPIRY=365
CONFIG_FILE="san.cnf"

# Generate a configuration file for the domain certificate
cat > $CONFIG_FILE <<EOF
[req]
default_bits = 2048
prompt = no
default_md = sha256
req_extensions = req_ext
distinguished_name = dn

[ dn ]
CN = $DOMAIN

[ req_ext ]
subjectAltName = @alt_names

[ alt_names ]
DNS.1 = $DOMAIN
EOF

# Create a private key for the root certificate
openssl genrsa -out $ROOT_CERT.key 2048

# Create a self-signed root certificate
openssl req -x509 -new -nodes -key $ROOT_CERT.key -sha256 -days $ROOT_CERT_EXPIRY -out $ROOT_CERT.pem -subj "/CN=My Root CA"

# Create a private key for the domain
openssl genrsa -out $DOMAIN.key 2048

# Create a certificate signing request (CSR) for the domain with SAN
openssl req -new -key $DOMAIN.key -out $DOMAIN.csr -config $CONFIG_FILE

# Create a certificate for the domain signed by the root certificate
openssl x509 -req -in $DOMAIN.csr -CA $ROOT_CERT.pem -CAkey $ROOT_CERT.key -CAcreateserial -out $DOMAIN.crt -days $DOMAIN_CERT_EXPIRY -sha256 -extfile $CONFIG_FILE -extensions req_ext

# Convert the private key to PKCS8 format for HttpClient
openssl pkcs8 -topk8 -inform PEM -outform PEM -in $DOMAIN.key -out $DOMAIN.pkcs8.key -nocrypt

# Convert the domain certificate and the private key to .pfx format for Kestrel
# TODO: Use a password and fetch it with AWS Secrets Manager
openssl pkcs12 -export -out $DOMAIN.pfx -inkey $DOMAIN.key -in $DOMAIN.crt -passout pass:"" #-password pass:your_password

# Clean up CSR and config file
rm $DOMAIN.csr $CONFIG_FILE

echo "Certificates created."