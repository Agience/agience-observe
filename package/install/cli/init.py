"""
init.py — Agience one-shot trust seed.

Generates all cryptographic key material, data directories, and the
platform authority manifest before any service starts. Skips files that
already exist, so it is safe to run repeatedly.

This is the only place key material is ever written. The backend mounts
the keys directory and reads keys but never generates them.

Keypairs, one per service:
- origin.private.pem  / origin.public.pem     Origin service identity
- mantle.private.pem  / mantle.public.pem     Mantle service identity
- chorus.private.pem  / chorus.public.pem     Chorus service identity
- crystal.private.pem / crystal.public.pem    Crystal service identity

Each service reads only its own private key. All four public keys are
embedded inline in `authority.manifest.json`, which Mantle reads on first
boot and seeds as the singleton `vnd.agience.authority+json` artifact.

Bootstrap token: a fresh single-use token is generated and printed once.
Its sha256 hash is written into the authority manifest. The token itself
is also written to `bootstrap.token` for capture by the operator. The
operator presents the cleartext token to `POST /auth/bootstrap/claim`,
which clears the hash from the authority artifact.
"""
import base64
import hashlib
import json
import os
import secrets
import string
import uuid
from pathlib import Path

from cryptography.fernet import Fernet
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric import rsa
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey

DATA_DIR = Path(os.getenv("AGIENCE_INIT_DATA_DIR", "/data"))
KEYS_DIR = DATA_DIR / "keys"

AUTHORITY_NAMESPACE = uuid.UUID("a91ec900-0000-4000-8000-000000000001")

# Default service URIs — overridable via env at init time so federated /
# remote deployments can stamp real URLs into the manifest.
DEFAULT_ORIGIN_URI = os.getenv("AUTHORITY_ORIGIN_URI", "http://origin:8080")
DEFAULT_MANTLE_URI  = os.getenv("AUTHORITY_MANTLE_URI",  "http://mantle:8081")
DEFAULT_CHORUS_URI = os.getenv("AUTHORITY_CHORUS_URI", "http://chorus:8082")
DEFAULT_CRYSTAL_URI = os.getenv("AUTHORITY_CRYSTAL_URI", "http://crystal:8085")
# lumen is the wisdom/inference tekton in chorus, not a standalone service — no URI, no keypair,
# no trust anchor.
DEFAULT_ISSUER     = os.getenv("AUTHORITY_ISSUER",     DEFAULT_ORIGIN_URI)

# The authority artifact's id, derived from the issuer.
#
# Deriving from the issuer makes the id say which network it belongs to: `origin.home.agience.ai`
# and `origin.agience.ai` are provably distinct authorities. A constant id would conflate any two
# authorities that shared it — harmless only while nothing can hold two authorities at once, and an
# ember can hold two the moment it joins a larger ground plane. The per-anchor JWKS does differ
# between authorities, and `ember/runtime/discovery.py::verify` checks it, but that leaves only that
# one check standing between two authorities that share an id.
#
# This derivation is stable: the same issuer always yields the same id, with no coordination needed.
AUTHORITY_ARTIFACT_ID = str(uuid.uuid5(AUTHORITY_NAMESPACE, DEFAULT_ISSUER))

_SERVICE_URIS = {
    "origin": DEFAULT_ORIGIN_URI,
    "mantle": DEFAULT_MANTLE_URI,
    "chorus": DEFAULT_CHORUS_URI,
    "crystal": DEFAULT_CRYSTAL_URI,
}


def b64url(b: bytes) -> str:
    return base64.urlsafe_b64encode(b).rstrip(b"=").decode()


def ensure_dirs() -> None:
    # "mantle" holds the standalone lattice volume (MANTLE_LATTICE_PATH=/app/.data/mantle/mantle.db).
    for d in ["keys", "minio", "mantle", "origin", "stream", "iris"]:
        (DATA_DIR / d).mkdir(parents=True, exist_ok=True)
    os.chmod(KEYS_DIR, 0o711)  # 711: owner=rwx, group=x, others=x — containers can access files by name but can't list dir

    # Container UID ownership — these services run as non-root.
    # MinIO (UID 1000)
    _chown_dir("minio", 1000, 1000)
    print("[init] Directories ready")


def _chown_dir(name: str, uid: int, gid: int) -> None:
    """Hand a data dir to the container UID that will own it.

    POSIX-only: `os.chown` does not exist on Windows, where ownership is meaningless anyway — there
    are no container UIDs on a dev box, so skipping is correct there, not a degradation.
    `AttributeError` is caught alongside `OSError` so a platform without `chown` degrades the same
    way rather than aborting the run."""
    path = DATA_DIR / name
    if not hasattr(os, "chown"):
        print(f"[init] Skipping chown for {path} — not a POSIX filesystem (no container UIDs here)")
        return
    try:
        os.chown(path, uid, gid)
    except (OSError, AttributeError) as exc:
        print(f"[init] Warning: could not chown {path}: {exc}")


def write_if_missing(path: Path, content: str, mode: int = 0o600) -> bool:
    if path.exists():
        print(f"[init] {path.name} already exists, skipping")
        return False
    path.write_text(content)
    os.chmod(path, mode)
    print(f"[init] Generated {path.name}")
    return True


def _gen_complex_password(length: int = 20) -> str:
    """Generate a password meeting common complexity requirements (upper, lower, digit, special)."""
    chars = string.ascii_letters + string.digits + "!@#$^&*"
    while True:
        pwd = "".join(secrets.choice(chars) for _ in range(length))
        if (
            any(c.isupper() for c in pwd)
            and any(c.islower() for c in pwd)
            and any(c.isdigit() for c in pwd)
            and any(c in "!@#$^&*" for c in pwd)
        ):
            return pwd


def gen_simple_secrets() -> None:
    write_if_missing(KEYS_DIR / "encryption.key", Fernet.generate_key().decode(), mode=0o400)
    write_if_missing(KEYS_DIR / "platform_internal.secret", secrets.token_urlsafe(48), mode=0o400)
    write_if_missing(KEYS_DIR / "inbound_nonce.secret", secrets.token_urlsafe(48), mode=0o400)
    # First-boot operator token — verified by Origin's /setup/complete endpoint.
    write_if_missing(KEYS_DIR / "setup.token", secrets.token_urlsafe(24), mode=0o400)
    # MinIO password: use env override on first boot (import/existing-data scenario), else generate
    minio_pass = os.getenv("MINIO_ROOT_PASSWORD") or _gen_complex_password()
    write_if_missing(KEYS_DIR / "minio.pass", minio_pass, mode=0o444)
    # Per-deployment namespace UUID for deterministic seed-artifact ID derivation
    # (uuid5). Minted here so services that mount the keys dir read-only can just
    # read it — mantle's get_instance_namespace() would otherwise try to write it
    # and crash on the RO keys mount ([Errno 30] Read-only file system).
    write_if_missing(KEYS_DIR / "instance.uuid", str(uuid.uuid4()), mode=0o444)


def _generate_rsa_keypair(label: str) -> rsa.RSAPrivateKey:
    """Generate an RSA-2048 keypair, write PEM files, return the private key for inline-JWKS export."""
    priv_path = KEYS_DIR / f"{label}.private.pem"
    pub_path  = KEYS_DIR / f"{label}.public.pem"
    if priv_path.exists() and pub_path.exists():
        print(f"[init] {label}.private.pem + {label}.public.pem already exist, skipping")
        return serialization.load_pem_private_key(priv_path.read_bytes(), password=None)

    private_key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    priv_path.write_text(
        private_key.private_bytes(
            encoding=serialization.Encoding.PEM,
            format=serialization.PrivateFormat.TraditionalOpenSSL,
            encryption_algorithm=serialization.NoEncryption(),
        ).decode()
    )
    os.chmod(priv_path, 0o400)
    pub_path.write_text(
        private_key.public_key().public_bytes(
            encoding=serialization.Encoding.PEM,
            format=serialization.PublicFormat.SubjectPublicKeyInfo,
        ).decode()
    )
    os.chmod(pub_path, 0o444)
    print(f"[init] Generated {label}.private.pem + {label}.public.pem")
    return private_key


def _public_key_to_jwk(public_key: rsa.RSAPublicKey, kid: str) -> dict:
    """Convert an RSA public key to a JWK (RFC 7517) entry."""
    numbers = public_key.public_numbers()
    n_bytes = numbers.n.to_bytes((numbers.n.bit_length() + 7) // 8, "big")
    e_bytes = numbers.e.to_bytes((numbers.e.bit_length() + 7) // 8, "big")
    return {
        "kty": "RSA",
        "alg": "RS256",
        "use": "sig",
        "kid": kid,
        "n": b64url(n_bytes),
        "e": b64url(e_bytes),
    }


def gen_service_keypairs() -> dict:
    """Generate per-service keypairs (origin, mantle, chorus, crystal) and return them keyed by name.

    Each service holds only its own private key (filesystem perms enforce this; volume mounts
    project the right key into the right container). Public keys go into the authority manifest
    so service-to-service auth works without any HTTP fetch.
    """
    return {
        "origin": _generate_rsa_keypair("origin"),
        "mantle":  _generate_rsa_keypair("mantle"),
        "chorus": _generate_rsa_keypair("chorus"),
        "crystal": _generate_rsa_keypair("crystal"),
    }


def _build_anchors(service_keys: dict) -> dict:
    """Build the `trust_anchors` map (inline JWKS per service) for the manifest."""
    return {
        name: {
            "uri": _SERVICE_URIS.get(name, ""),
            "jwks": {"keys": [_public_key_to_jwk(key.public_key(), f"{name}-1")]},
        }
        for name, key in service_keys.items()
    }


def gen_authority_manifest(service_keys: dict) -> None:
    """Write `authority.manifest.json` to KEYS_DIR.

    Mantle reads this file on first boot and seeds it as the singleton
    `vnd.agience.authority+json` artifact in Arango. Idempotent: if the file already
    exists, the existing token hash is preserved (the operator may not have claimed yet).
    """
    manifest_path = KEYS_DIR / "authority.manifest.json"
    token_path    = KEYS_DIR / "bootstrap.token"
    anchors = _build_anchors(service_keys)

    if manifest_path.exists():
        # Merge any newly-added anchors (e.g. a service added in a later release)
        # into the existing manifest without disturbing issuer / bootstrap token /
        # existing anchors. This is the non-destructive upgrade path.
        existing = json.loads(manifest_path.read_text())
        existing_anchors = existing.get("trust_anchors", {})
        added = [n for n in anchors if n not in existing_anchors]
        if not added:
            print("[init] authority.manifest.json already current, skipping")
            return
        existing_anchors.update({n: anchors[n] for n in added})
        existing["trust_anchors"] = existing_anchors
        try:
            os.chmod(manifest_path, 0o640)
        except OSError:
            pass
        manifest_path.write_text(json.dumps(existing, indent=2) + "\n")
        try:
            os.chmod(manifest_path, 0o440)
        except OSError:
            pass
        print(f"[init] Added trust anchors to authority.manifest.json: {added}")
        return

    # Generate the bootstrap token (single-use, claimed by first operator).
    bootstrap_token = secrets.token_urlsafe(32)
    token_hash = hashlib.sha256(bootstrap_token.encode()).hexdigest()
    # sha256 is used here (not bcrypt) so the manifest can be loaded without a runtime
    # password-hashing dependency in the init container. Origin's claim handler also uses
    # sha256 for verification — the token has 256 bits of entropy and lives only in operator
    # memory until claim, so the hash is solely about not storing the cleartext at rest.

    write_if_missing(token_path, bootstrap_token, mode=0o400)
    print("=" * 70)
    print("[init] BOOTSTRAP TOKEN (single-use, capture now):")
    print(f"       {bootstrap_token}")
    print("       Present this to POST /auth/bootstrap/claim to create the first operator.")
    print(f"       Also written to {token_path} (mode 0400).")
    print("=" * 70)

    manifest = {
        "artifact_id":      AUTHORITY_ARTIFACT_ID,
        "content_type":     "application/vnd.agience.authority+json",
        "schema_version":   1,
        "issuer":           DEFAULT_ISSUER,
        "trust_anchors":    anchors,
        "bootstrap_token_hash": token_hash,
    }

    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n")
    os.chmod(manifest_path, 0o440)
    print(f"[init] Generated {manifest_path.name} (authority artifact id: {AUTHORITY_ARTIFACT_ID})")


def gen_licensing_keys() -> None:
    priv_path = KEYS_DIR / "licensing_private.pem"
    pub_path = KEYS_DIR / "licensing_public.pem"
    anchors_path = KEYS_DIR / "licensing_trust_anchors.json"

    if priv_path.exists() and pub_path.exists():
        print("[init] licensing keys already exist, skipping")
        if not anchors_path.exists():
            _write_trust_anchors(
                anchors_path,
                serialization.load_pem_public_key(pub_path.read_bytes()),
            )
        return

    private_key = Ed25519PrivateKey.generate()
    public_key = private_key.public_key()
    priv_path.write_text(
        private_key.private_bytes(
            encoding=serialization.Encoding.PEM,
            format=serialization.PrivateFormat.PKCS8,
            encryption_algorithm=serialization.NoEncryption(),
        ).decode()
    )
    os.chmod(priv_path, 0o400)
    pub_path.write_text(
        public_key.public_bytes(
            encoding=serialization.Encoding.PEM,
            format=serialization.PublicFormat.SubjectPublicKeyInfo,
        ).decode()
    )
    os.chmod(pub_path, 0o444)
    _write_trust_anchors(anchors_path, public_key)
    print("[init] Generated licensing_private.pem + licensing_public.pem + licensing_trust_anchors.json")


def _write_trust_anchors(path: Path, public_key) -> None:
    pub_bytes = public_key.public_bytes(
        encoding=serialization.Encoding.Raw,
        format=serialization.PublicFormat.Raw,
    )
    anchors = {
        "schema_version": "1",
        "keys": [{"kid": "lic-s1", "alg": "EdDSA", "public_key": b64url(pub_bytes)}],
    }
    path.write_text(json.dumps(anchors, indent=2) + "\n")
    os.chmod(path, 0o444)
    print("[init] Generated licensing_trust_anchors.json")


if __name__ == "__main__":
    # Every step below is individually idempotent (write_if_missing / exists
    # checks), so we run them on every boot rather than short-circuiting on a
    # `.initialized` sentinel. This lets a newer init image add newly-required
    # key material (e.g. a new service keypair) to an existing deployment on the
    # next deploy, rather than leaving it ungenerated behind a sentinel that has
    # already fired.
    ensure_dirs()
    gen_simple_secrets()
    service_keys = gen_service_keypairs()
    gen_licensing_keys()
    gen_authority_manifest(service_keys)
    print("[init] Complete")
