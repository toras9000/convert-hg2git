FROM ubuntu:22.04

ARG DEBIAN_FRONTEND=noninteractive

RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates mercurial git python3 \
 && apt-get autoremove -y \
 && apt-get clean \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /work

ARG TARGET_BRANCH=master
RUN git clone --depth 1 --branch "$TARGET_BRANCH" https://github.com/frej/fast-export.git \
 && rm -rf /work/fast-export/.git* \
 && rm -rf /work/fast-export/tests
