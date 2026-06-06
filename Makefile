.PHONY: help build build-release native test clean pack install uninstall reinstall setup all

# Variables
VERSION   := $(shell cat version)
NUPKG_DIR := ./nupkgs
TOOL_NAME := sl
PKG_ID    := sherlock-tools
SOLUTION  := Sherlock.slnx
CLI_PROJ  := src/Sherlock.CLI/Sherlock.CLI.csproj

help: ## Show this help message
	@echo 'Usage: make [target]'
	@echo ''
	@echo 'Available targets:'
	@grep -E '^[a-zA-Z_-]+:.*?## .*$$' $(MAKEFILE_LIST) | awk 'BEGIN {FS = ":.*?## "}; {printf "  %-18s %s\n", $$1, $$2}'

build: ## Build the solution in Debug mode
	dotnet build $(SOLUTION) -c Debug

build-release: ## Build the solution in Release mode
	dotnet build $(SOLUTION) -c Release

native: ## Build the native profiler and stage it under runtimes/<host-rid>/native
	bash src/native/build.sh
	@set -e; \
	os=$$(uname -s); arch=$$(uname -m); \
	case "$$os" in Darwin) rid_os=osx; lib=libSherlockProfiler.dylib;; *) rid_os=linux; lib=libSherlockProfiler.so;; esac; \
	case "$$arch" in arm64|aarch64) rid_arch=arm64;; *) rid_arch=x64;; esac; \
	dest=src/Sherlock.CLI/runtimes/$$rid_os-$$rid_arch/native; \
	mkdir -p $$dest; cp src/native/bin/$$lib $$dest/; \
	echo "staged $$lib -> $$dest"

test: ## Run all tests
	dotnet test $(SOLUTION)

clean: ## Clean build artifacts
	dotnet clean $(SOLUTION) || true
	rm -rf $(NUPKG_DIR)
	rm -rf */*/bin */*/obj

pack: clean native build-release ## Pack the CLI as a NuGet tool package (incl. host-RID profiler)
	dotnet pack $(CLI_PROJ) -c Release -o $(NUPKG_DIR) /p:Version=$(VERSION)
	@echo ""
	@echo "Package created: $(NUPKG_DIR)/$(PKG_ID).$(VERSION).nupkg"

install: pack ## Install the tool globally
	dotnet tool uninstall -g $(PKG_ID) 2>/dev/null || true
	dotnet tool install --global --add-source $(NUPKG_DIR) $(PKG_ID) --version $(VERSION)
	@echo ""
	@echo "Installed! Use: $(TOOL_NAME)"
	@echo "If not found, add ~/.dotnet/tools to your PATH:"
	@echo "  echo 'export PATH=\"\$$PATH:\$$HOME/.dotnet/tools\"' >> ~/.zprofile && source ~/.zprofile"

uninstall: ## Uninstall the global tool
	dotnet tool uninstall -g $(PKG_ID)

reinstall: uninstall install ## Reinstall the tool (clean install)

setup: ## Restore packages
	dotnet restore $(SOLUTION)

# Release: bump the version file
bump-major: ## Bump major version (1.2.3 -> 2.0.0)
	@echo $(VERSION) | awk -F. '{print $$1+1".0.0"}' > version
	@echo "Version: $(VERSION) -> $$(cat version)"

bump-minor: ## Bump minor version (1.2.3 -> 1.3.0)
	@echo $(VERSION) | awk -F. '{print $$1"."$$2+1".0"}' > version
	@echo "Version: $(VERSION) -> $$(cat version)"

bump-patch: ## Bump patch version (1.2.3 -> 1.2.4)
	@echo $(VERSION) | awk -F. '{print $$1"."$$2"."$$3+1}' > version
	@echo "Version: $(VERSION) -> $$(cat version)"

all: clean build test ## Clean, build, and test