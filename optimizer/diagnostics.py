"""Rotating diagnostic logs; request credentials and payloads are never logged."""
import logging
from logging.handlers import RotatingFileHandler
from pathlib import Path


logger = logging.getLogger("qfrey")
logger.setLevel(logging.INFO)
logger.propagate = False


def configure_logging(directory):
    path = Path(directory) / "logs/qfrey-tuner.log"
    path.parent.mkdir(parents=True, exist_ok=True)
    if any(getattr(h, "baseFilename", None) == str(path.resolve()) for h in logger.handlers):
        return path
    for handler in list(logger.handlers):
        logger.removeHandler(handler)
        handler.close()
    handler = RotatingFileHandler(path, maxBytes=2_000_000, backupCount=3, encoding="utf-8")
    handler.setFormatter(logging.Formatter("%(asctime)s %(levelname)s %(threadName)s %(message)s"))
    logger.addHandler(handler)
    logger.info("Application diagnostics initialized")
    return path
