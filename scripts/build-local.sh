#!/bin/bash
# Local build script for testing CI/CD workflow

set -e

VERSION=$(git describe --tags --always --dirty 2>/dev/null || echo "dev")
COMMIT=$(git rev-parse --short HEAD 2>/dev/null || echo "unknown")
BUILD_DATE=$(date -u +"%Y-%m-%dT%H:%M:%SZ")

echo "Building mcp-sidecar"
echo "  Version: $VERSION"
echo "  Commit:  $COMMIT"
echo "  Date:    $BUILD_DATE"
echo

cd "$(dirname "$0")/src/McpSidecar"

# Build for current platform
RUNTIME=$(dotnet --info | grep "RID:" | head -1 | awk '{print $2}')
if [ -z "$RUNTIME" ]; then
    echo "Could not detect runtime, using linux-x64"
    RUNTIME="linux-x64"
fi

echo "Building for $RUNTIME..."
dotnet publish \
    --configuration Release \
    --runtime "$RUNTIME" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:PublishTrimmed=true \
    -p:VersionPrefix="$VERSION" \
    -p:SourceRevisionId="$COMMIT" \
    -p:BuildDate="$BUILD_DATE" \
    --output ./bin/publish

echo
echo "Build complete: src/McpSidecar/bin/publish/"
ls -lh ./bin/publish/
