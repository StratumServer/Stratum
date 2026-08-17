# Stratum development targets.
# Requires: .NET 10 SDK, bash, python3, git, curl.
# Windows: use Git Bash, WSL, or run the .ps1 scripts directly.

CONFIGURATION ?= Release
VERSION ?= 1.22.7
SERVER_ARCHIVE ?=
CLIENT_LIB_DIR ?=
# Embeds this working tree's compiled DLLs into StratumServer so
# PatchedFileOverlay actually overlays them at runtime. Without this, the
# server silently falls back to vanilla's unpatched VintagestoryLib.dll from
# the downloaded archive; the failure mode is a working build that boots but
# runs none of this repo's patches.
EMBED_PATCHED_FILES ?= true

BOOTSTRAP_ARGS :=
ifneq ($(SERVER_ARCHIVE),)
  BOOTSTRAP_ARGS += --server-archive $(SERVER_ARCHIVE)
endif
ifneq ($(CLIENT_LIB_DIR),)
  BOOTSTRAP_ARGS += --client-lib-dir $(CLIENT_LIB_DIR)
endif
ifneq ($(VERSION),1.22.7)
  BOOTSTRAP_ARGS += --version $(VERSION)
endif

.PHONY: bootstrap build smoke clean refresh help

help: ## Show available targets
	@grep -E '^[a-z-]+:.*##' $(MAKEFILE_LIST) | sort | awk -F ':.*## ' '{printf "  %-12s %s\n", $$1, $$2}'

bootstrap: ## Download, decompile, and apply patches
	bash scripts/bootstrap.sh $(BOOTSTRAP_ARGS)

build: ## Build Release (runs bootstrap if working tree is missing)
	@if [ ! -f VintagestoryApi/VintagestoryAPI.csproj ]; then $(MAKE) bootstrap; fi
	dotnet build VintageStory.slnx -c $(CONFIGURATION)
# Second pass, only if embedding is on: StratumServer's EmbeddedResource list
# points at sibling projects' bin output by raw path, not a ProjectReference,
# so on a build where those outputs do not exist yet (a fresh bootstrap, or
# after clean/refresh) the embed can race the projects that produce them.
# The pass above guarantees every output exists before this one tries to
# embed it.
ifeq ($(EMBED_PATCHED_FILES),true)
	dotnet build VintageStory.slnx -c $(CONFIGURATION) -p:EmbedPatchedFiles=true
endif

smoke: build ## Build and boot-test the server
	bash scripts/smoke-test.sh

clean: ## Remove intermediate build files (use refresh for full reset)
	find . -type d -name obj -not -path './.baseline/*' -not -path './.vanilla/*' | xargs -r rm -rf

refresh: ## Force full re-bootstrap from scratch
	bash scripts/bootstrap.sh --refresh $(BOOTSTRAP_ARGS)
