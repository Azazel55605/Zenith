"""Keep UART text offsets identical between log snapshots and incremental waits."""
def serial_log(machine):
    if not machine.serial.exists():
        return ''
    # Path.read_text uses universal newline translation. The incremental reader
    # decodes raw bytes, so collapsing CRLF there moves every later offset back
    # into earlier records. Repeated status labels can then pass on stale output.
    with machine.serial.open('r', encoding='utf-8', errors='replace', newline='') as stream:
        return stream.read()


def install(machine_type):
    machine_type.log = serial_log
