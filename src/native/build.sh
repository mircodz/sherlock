#!/bin/bash
set -e

# Always build relative to this script, regardless of the caller's CWD.
cd "$(dirname "$0")"

printf '  Building ...'

if [ ! -d "bin/" ]; then
    mkdir bin/
fi

pushd bin

export CC=/usr/bin/clang
export CXX=/usr/bin/clang++
cmake ../ -DCMAKE_BUILD_TYPE=Debug -DBUILD_SHARED_LIBS=OFF

make -j

popd