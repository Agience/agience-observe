# Changelog

All notable changes to `agience-observe` are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

## [202611.12.1] - 2026-11-12

### Fixed

- The installer's `init.py` gives each persona its own keypair and manifest anchor, as `agience-cloud/deploy/init.py` does (the two are byte-identical).

### Changed

- The manager gives an Origin instance its exact redirect URIs (`CLIENT_REDIRECT_URIS`): its own sign-in, passkey and step-up pages. It no longer sets `FRONTEND_URI`, which Origin does not read.

## [2026.10.0] - 2026-10-09

### Changed

- The "Declaration of generative AI use" section of `README.md` now lists one more model version among those used in preparing the work.
