#!/bin/bash

# Define variables
OUTPUT_DIR="./certs"
ROOT_CERT="fakeroot"
DOMAIN="lambdadispatch.local"
ROOT_CERT_EXPIRY=365
DOMAIN_CERT_EXPIRY=365
CONFIG_FILE="$OUTPUT_DIR/san.cnf"

# Create the output directory if it doesn't exist
mkdir -p $OUTPUT_DIR

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
openssl genrsa -out $OUTPUT_DIR/$ROOT_CERT.key 2048

# Create a self-signed root certificate
openssl req -x509 -new -nodes -key $OUTPUT_DIR/$ROOT_CERT.key -sha256 -days $ROOT_CERT_EXPIRY -out $OUTPUT_DIR/$ROOT_CERT.pem -subj "/CN=My Root CA"

# Add the root certificate to the system's trust store
# This is useful if you'd prefer to observe the traffic with Fiddler (but I don't think it will show this issue)
# sudo security add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain $OUTPUT_DIR/$ROOT_CERT.pem

# Create a private key for the domain
openssl genrsa -out $OUTPUT_DIR/$DOMAIN.key 2048

# Create a certificate signing request (CSR) for the domain with SAN
openssl req -new -key $OUTPUT_DIR/$DOMAIN.key -out $OUTPUT_DIR/$DOMAIN.csr -config $CONFIG_FILE

# Create a certificate for the domain signed by the root certificate
openssl x509 -req -in $OUTPUT_DIR/$DOMAIN.csr -CA $OUTPUT_DIR/$ROOT_CERT.pem -CAkey $OUTPUT_DIR/$ROOT_CERT.key -CAcreateserial -CAserial $OUTPUT_DIR/$ROOT_CERT.srl -out $OUTPUT_DIR/$DOMAIN.crt -days $DOMAIN_CERT_EXPIRY -sha256 -extfile $CONFIG_FILE -extensions req_ext
# Convert the private key to PKCS8 format for HttpClient
openssl pkcs8 -topk8 -inform PEM -outform PEM -in $OUTPUT_DIR/$DOMAIN.key -out $OUTPUT_DIR/$DOMAIN.pkcs8.key -nocrypt

# Convert the domain certificate and the private key to .pfx format for Kestrel
openssl pkcs12 -export -out $OUTPUT_DIR/$DOMAIN.pfx -inkey $OUTPUT_DIR/$DOMAIN.key -in $OUTPUT_DIR/$DOMAIN.crt -passout pass:"" #-password pass:your_password

# Clean up CSR and config file
rm $OUTPUT_DIR/$DOMAIN.csr $CONFIG_FILE

echo "Certificates created in $OUTPUT_DIR."