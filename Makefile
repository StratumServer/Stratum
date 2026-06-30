# Stratum development targets.
# Requires: .NET 10 SDK, bash, python3, git, curl.
# Windows: use Git Bash, WSL, or run the .ps1 scripts directly.

CONFIGURATION ?= Release
VERSION ?= 1.22.3
SERVER_ARCHIVE ?=
CLIENT_LIB_DIR ?=

BOOTSTRAP_ARGS :=
ifneq ($(SERVER_ARCHIVE),)
  BOOTSTRAP_ARGS += --server-archive $(SERVER_ARCHIVE)
endif
ifneq ($(CLIENT_LIB_DIR),)
  BOOTSTRAP_ARGS += --client-lib-dir $(CLIENT_LIB_DIR)
endif
ifneq ($(VERSION),1.22.3)
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

smoke: build ## Build and boot-test the server
	bash scripts/smoke-test.sh

clean: ## Remove intermediate build files (use refresh for full reset)
	find . -type d -name obj -not -path './.baseline/*' -not -path './.vanilla/*' | xargs -r rm -rf

refresh: ## Force full re-bootstrap from scratch
	bash scripts/bootstrap.sh --refresh $(BOOTSTRAP_ARGS)
