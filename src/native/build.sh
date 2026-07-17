#!/bin/bash
set -e

# Always build relative to this script, regardless of the caller's CWD.
cd "$(dirname "$0")"

# Optimized by default (the allocation hot path needs it); `./build.sh debug` for an -O0 debug build.
BUILD_TYPE=Release
if [ "$1" = "debug" ]; then
    BUILD_TYPE=Debug
fi

printf '  Building (%s) ...' "$BUILD_TYPE"

if [ ! -d "bin/" ]; then
    mkdir bin/
fi

pushd bin

export CC=/usr/bin/clang
export CXX=/usr/bin/clang++
cmake ../ -DCMAKE_BUILD_TYPE="$BUILD_TYPE" -DBUILD_SHARED_LIBS=OFF

make -j

popd