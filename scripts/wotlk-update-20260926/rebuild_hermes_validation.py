#!/usr/bin/env python3
"""Revalidate Hermes after a candidate-only correction, inside PrivateNetwork."""
import prepare_hermes
import validate_candidate

if __name__ == '__main__':
    validate_candidate.private_network()
    prepare_hermes.build()
    validate_candidate.hermes_tests()
    validate_candidate.hermes_publish()
