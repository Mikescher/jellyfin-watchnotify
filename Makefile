PROJECT        = Jellyfin.Plugin.WatchNotify/Jellyfin.Plugin.WatchNotify.csproj
ASSEMBLY       = Jellyfin.Plugin.WatchNotify
FRAMEWORK      = net9.0
VERSION       ?= $(shell sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)
CHANGELOG     ?=
JELLYFIN_CONFIG ?= $(HOME)/.config/jellyfin

.PHONY: build publish package release deploy clean

build:
	dotnet build $(PROJECT) -c Release

publish:
	dotnet publish $(PROJECT) -c Release -f $(FRAMEWORK) -o artifacts/publish

# Builds artifacts/watchnotify_<version>.zip and prints its md5.
package:
	python3 scripts/package.py --changelog "$(CHANGELOG)"

# Bumps the version, packages it and records it in manifest.json. Push the
# resulting commit and the v<version> tag; CI builds the authoritative zip and
# rewrites the manifest entry with that artifact's checksum.
release:
	python3 scripts/package.py --version "$(VERSION)" --changelog "$(CHANGELOG)" --manifest

# Drops the assembly into a local server. A folder without meta.json is still
# loaded, so no packaging step is needed while iterating.
deploy: publish
	mkdir -p "$(JELLYFIN_CONFIG)/plugins/$(ASSEMBLY)_$(VERSION)"
	cp artifacts/publish/$(ASSEMBLY).dll "$(JELLYFIN_CONFIG)/plugins/$(ASSEMBLY)_$(VERSION)/"
	@echo "Installed to $(JELLYFIN_CONFIG)/plugins/$(ASSEMBLY)_$(VERSION) — restart Jellyfin"

clean:
	rm -rf artifacts
	dotnet clean $(PROJECT) -c Release
