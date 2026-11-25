"""Developer-only loopback fault proxy; never included in the volunteer ZIP.

Connect the guest to 127.0.0.1:42872 and host on 127.0.0.1:42871.
Write hold, drop, resume, or stop to CONTROL/command to exercise an outstanding
request and reconnection without changing firewall rules or the game process.
"""
import argparse
import asyncio
from datetime import datetime, timezone
from pathlib import Path


async def main(args):
    args.control.mkdir(parents=True, exist_ok=True)
    peers = set()
    release = asyncio.Event()
    release.set()
    stopped = asyncio.Event()

    def log(message):
        line = datetime.now(timezone.utc).isoformat() + ' ' + message
        print(line, flush=True)
        with (args.control / 'proxy.log').open('a', encoding='utf-8') as output:
            output.write(line + '\n')

    async def connected(reader, writer):
        other = None
        try:
            incoming, other = await asyncio.open_connection('127.0.0.1', args.target)
            peers.update((writer, other))
            log('connected')

            async def pump(source, destination, returning):
                while chunk := await source.read(65536):
                    if returning:
                        await release.wait()
                    destination.write(chunk)
                    await destination.drain()

            tasks = [asyncio.create_task(pump(reader, other, False)),
                     asyncio.create_task(pump(incoming, writer, True))]
            await asyncio.wait(tasks, return_when=asyncio.FIRST_COMPLETED)
            for task in tasks:
                task.cancel()
            await asyncio.gather(*tasks, return_exceptions=True)
        except (OSError, asyncio.CancelledError) as error:
            log(type(error).__name__)
        finally:
            for stream in (writer, other):
                if stream:
                    peers.discard(stream)
                    stream.close()
            log('disconnected')

    async with await asyncio.start_server(connected, '127.0.0.1', args.listen):
        log(f'listening 127.0.0.1:{args.listen} -> 127.0.0.1:{args.target}')
        while not stopped.is_set():
            command = args.control / 'command'
            if command.exists():
                action = command.read_text(encoding='utf-8-sig').strip()
                command.unlink()
                if action == 'hold':
                    release.clear()
                elif action == 'resume':
                    release.set()
                elif action in ('drop', 'stop'):
                    for peer in list(peers):
                        peer.close()
                    release.set()
                    if action == 'stop':
                        stopped.set()
                else:
                    raise ValueError('Use hold, drop, resume, or stop')
                log(action)
            await asyncio.sleep(.05)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('control', type=Path)
    parser.add_argument('--listen', type=int, default=42872)
    parser.add_argument('--target', type=int, default=42871)
    asyncio.run(main(parser.parse_args()))
