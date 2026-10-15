"""Builds the contract-shaped result a worker returns for one task (``TaskResultManifest``)."""

from __future__ import annotations

import hashlib
import json

from . import registry
from .residential import DetectionResult

CONTRACT_VERSION = "0.1.0"
ALGORITHM_NAME = "residential-label-counter"
ALGORITHM_VERSION = "0.1.0"
TASK_TYPE = "detect-residential-labels"


def params_hash() -> str:
    """A hash of everything that changes what the detector emits: its version, grammar and note rules."""
    material = {
        "version": ALGORITHM_VERSION,
        "labels": [(r.name, r.pattern.pattern, r.type, r.confidence, r.two_dimension) for r in registry.LABEL_RULES],
        "notes": [(r.name, r.pattern.pattern, r.type, r.confidence) for r in registry.NOTE_RULES],
        "cfm": registry.CFM_LINE.pattern,
    }
    return hashlib.sha256(json.dumps(material, sort_keys=True).encode("utf-8")).hexdigest()


def build_manifest(result: DetectionResult, task_public_id: str) -> dict:
    return {
        "contractVersion": CONTRACT_VERSION,
        "taskType": TASK_TYPE,
        "taskPublicId": task_public_id,
        "producer": {
            "kind": "Algorithm",
            "algorithmName": ALGORITHM_NAME,
            "algorithmVersion": ALGORITHM_VERSION,
            "paramsHash": params_hash(),
        },
        "detections": result.detections,
        "findings": result.findings,
    }
