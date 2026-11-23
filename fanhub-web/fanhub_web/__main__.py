"""fanhub-web: веб-интерфейс для контроллера MKS Fan Hub (замена Windows-трея).

Запуск:
    python -m fanhub_web --config /etc/fanhub-web/config.json
"""
from __future__ import annotations

import argparse
import os
import sys

from .config import Config


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--config", default=os.environ.get("FANHUB_CONFIG", "config.json"))
    ap.add_argument("--host", default=None)
    ap.add_argument("--port", type=int, default=None)
    args = ap.parse_args()

    config = Config(args.config)
    host = args.host or config.data["web_host"]
    port = args.port or config.data["web_port"]

    import uvicorn
    from .server import create_app

    if not os.path.exists(config.path):
        config.save()

    uvicorn.run(create_app(config), host=host, port=port, log_level="warning")


if __name__ == "__main__":
    main()
