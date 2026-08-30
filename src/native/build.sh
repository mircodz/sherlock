#!/bin/bash
set -e

cd "$(dirname "$0")"

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