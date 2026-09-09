"""The installer's tables must describe the platform that actually exists.

`PACKAGES`, `REQUIRED`, `SERVICES` and the checkout map in `resolve_sources` are four statements
about the same five components, written in four places and in three different spellings — a
repository name, a distribution name, and an import path. Nothing checked that they agreed, and the
failure mode is not a red test: it is a person running `agience install` and getting an environment
that is missing a service, or one that starts and cannot import its own app.

These checks are cheap and they are structural. None of them installs anything or opens a socket.
"""

from __future__ import annotations

import importlib.util
import pathlib
import sys

import pytest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import agience  # noqa: E402


# ── the tables agree with each other ────────────────────────────────────────────────────────────

def test_every_package_label_is_unique():
    labels = [label for label, _ in agience.PACKAGES]
    assert len(labels) == len(set(labels)), (
        "two entries share a label, and `resolve_sources` keys its checkout map on the label — "
        "a duplicate silently drops one component from a source install")


def test_the_checkout_map_covers_exactly_the_git_packages(tmp_path):
    """A source install and a git install must produce the same set of components.

    `resolve_sources` builds its own list rather than deriving one, so the two can drift. When they
    do, `agience install --source` quietly omits whatever the map forgot and the missing package
    resolves from an index that does not carry it.
    """
    for name in ("agience-prism/py", "agience-crystal", "agience-mantle",
                 "agience-origin", "agience-ember"):
        (tmp_path / name).mkdir(parents=True)
        (tmp_path / name / "pyproject.toml").write_text("[project]\n", encoding="utf-8")

    from_source = agience.resolve_sources(str(tmp_path))
    assert [label for label, _ in from_source] == [label for label, _ in agience.PACKAGES], (
        "the checkout install and the git install name different components, or name them in a "
        "different order")


def test_a_partial_checkout_is_refused(tmp_path):
    """The control. Without this the test above passes against a map that checks nothing."""
    for name in ("agience-prism/py", "agience-crystal"):
        (tmp_path / name).mkdir(parents=True)
        (tmp_path / name / "pyproject.toml").write_text("[project]\n", encoding="utf-8")

    with pytest.raises(SystemExit):
        agience.resolve_sources(str(tmp_path))


def test_required_names_are_distributions_not_repositories():
    """`REQUIRED` is read against `*.dist-info` on disk, so it must be the distribution spelling.

    prism is the one where the two differ — repository `agience-prism-py`, distribution
    `agience-prism` — and naming a repository here makes `installed_version` return None forever,
    so a complete install reports itself incomplete.
    """
    assert "agience-prism-py" not in agience.REQUIRED
    for name in agience.REQUIRED:
        assert name.startswith("agience-"), name


# ── the services describe something that can start ──────────────────────────────────────────────

def test_each_service_gives_exactly_one_launch_shape():
    for service in agience.SERVICES:
        assert (service.app is None) != (service.module is None), (
            f"{service.name} gives both an app and a module, or neither")


def test_no_two_services_share_a_port():
    ports = [s.port for s in agience.SERVICES]
    assert len(ports) == len(set(ports)), (
        f"two services bind the same port: {sorted(ports)}. The second to start fails, and it "
        "fails after the first has already reported healthy")


def test_every_service_command_names_the_python_it_was_given(tmp_path):
    python = tmp_path / "python"
    for service in agience.SERVICES:
        command = service.command(python)
        assert command[0] == str(python), (
            f"{service.name} does not launch with the virtualenv's interpreter, so it would run "
            "against whatever python is on PATH")
        assert "--port" in command and str(service.port) in command


@pytest.mark.parametrize("service", agience.SERVICES, ids=lambda s: s.name)
def test_every_service_target_is_importable(service):
    """The app or module a service names must exist.

    Checked by import machinery rather than by importing: this file runs in the developer
    workspace, where the packages are on the path but starting them is not free. A name that
    resolves to nothing here resolves to nothing in the virtualenv too.
    """
    target = service.app.split(":")[0] if service.app else service.module[0]
    assert importlib.util.find_spec(target) is not None, (
        f"{service.name} names `{target}`, which is not importable. The service would fail at "
        "launch, after the installer had reported success")


def test_the_health_paths_are_paths():
    for service in agience.SERVICES:
        if service.health is not None:
            assert service.health.startswith("/"), (
                f"{service.name}: {service.health!r} is joined onto a base URL, so it must be a "
                "path")
