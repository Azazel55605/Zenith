#!/usr/bin/env python3
"""Qualify material frames through the signed/installed UI fixture, including opaque fallback."""
from pathlib import Path
import runpy
import sys
sys.argv.append('--materials')
runpy.run_path(str(Path(__file__).with_name('ui-gallery-smoke.py')),run_name='__main__')
