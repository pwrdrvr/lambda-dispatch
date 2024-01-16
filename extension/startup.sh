#!/bin/bash

#
# This is only used for testing with docker-compose
#

# Start the node process in the background
node dist/app.cjs &

# Execute /opt/extensions/lambda-dispatch
exec /opt/extensions/lambda-dispatch
