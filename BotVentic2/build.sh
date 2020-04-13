#!/bin/bash
TAG=$(git log --format="%H" -n 1)
git pull && docker image build --pull -t botventic:$TAG . && \
docker tag botventic:$TAG docker.pkg.github.com/3ventic/botventiccore/botventic:$TAG && \
docker push docker.pkg.github.com/3ventic/botventiccore/botventic:$TAG && \
docker tag botventic:$TAG docker.pkg.github.com/3ventic/botventiccore/botventic:latest && \
docker push docker.pkg.github.com/3ventic/botventiccore/botventic:latest
