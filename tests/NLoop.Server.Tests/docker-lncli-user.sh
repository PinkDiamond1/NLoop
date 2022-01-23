#!/bin/bash

docker-compose exec -T lnd_user lncli --tlscertpath=/data/tls.cert --macaroonpath=/data/admin.macaroon --rpcserver=localhost:32777 $@ | sed 's/\t//g'
