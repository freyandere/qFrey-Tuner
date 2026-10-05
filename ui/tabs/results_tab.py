"""Readable preference review and separate measured outcomes."""
import tkinter as tk
import customtkinter as ctk
from ui.tabs.benchmark_tab import card

LABELS = {
    'up_limit': ('Upload speed limit', 'Leave room for browsing and other apps.'),
    'dl_limit': ('Download speed limit', 'Maximum download traffic allowed.'),
    'max_connec': ('Total peer connections', 'How many other torrent users can connect at once.'),
    'max_connec_per_torrent': ('Connections per download', 'Share connections across your torrents.'),
    'max_uploads': ('Simultaneous uploads', 'Share your upload capacity across peers.'),
    'max_uploads_per_torrent': ('Uploads per torrent', 'Avoid splitting upload speed too thinly.'),
    'queueing_enabled': ('Manage active torrents', 'Queue extra torrents instead of running everything at once.'),
    'max_active_downloads': ('Downloads running at once', 'Keep downloads from competing for resources.'),
    'max_active_uploads': ('Uploads running at once', 'Limit how many torrents share your upload capacity.'),
    'max_active_torrents': ('Torrents running at once', 'The combined download and upload allowance.'),
    'preallocate_all': ('Reserve file space early', 'Allocate space before the download grows.'),
    'async_io_threads': ('Disk workers', 'How many tasks can handle disk operations.'),
    'bittorrent_protocol': ('Connection method', 'Choose how torrent traffic reaches peers.'),
    'encryption': ('Encrypted peer connections', 'Control which peer connections are accepted.'),
    'dht': ('Find peers through the network', 'Discover peers without relying only on a tracker.'),
    'pex': ('Find peers through other peers', 'Exchange peer addresses with connected users.'),
    'lsd': ('Find peers on your local network', 'Discover nearby torrent users.'),
    'anonymous_mode': ('Reduce identifying information', 'Reduce client details shared with peers; this does not hide your IP.'),
    'current_network_interface': ('Use this network adapter', 'Keep torrent traffic on the selected connection.'),
    'listen_port': ('Incoming connection port', 'The port peers use to connect to you.'),
    'random_port': ('Change port at startup', 'Whether qBittorrent chooses a new port on launch.'),
    'disk_cache': ('Download memory buffer', 'Hold data in memory before writing it to disk.'),
    'enable_os_cache': ('Use system disk caching', 'Allow the operating system to buffer disk data.'),
    'enable_coalesce_read_write': ('Combine small disk tasks', 'Group small reads and writes.'),
    'limit_utp_rate': ('Include all traffic in speed limits', 'Apply limits to the alternate torrent transport too.'),
    'send_buffer_watermark': ('Outgoing data buffer', 'Data prepared in advance for peers.'),
    'send_buffer_low_watermark': ('Minimum outgoing buffer', 'Keep some data ready for upload.'),
    'send_buffer_watermark_factor': ('Buffer scaling', 'How the outgoing buffer grows with speed.'),
    'socket_backlog_size': ('Waiting connection capacity', 'How many incoming connections can wait to be accepted.'),
    'connection_speed': ('New connections each second', 'How quickly qBittorrent searches for more peers.'),
}


def display(key, value):
    if isinstance(value, bool):
        return 'On' if value else 'Off'
    if key in ('up_limit', 'dl_limit'):
        return 'Unlimited' if value == 0 else f'{value / 1024**2:.2f} MiB/s'
    if key == 'bittorrent_protocol':
        return {0: 'Both methods', 1: 'TCP', 2: 'µTP'}.get(value, str(value))
    if key == 'encryption':
        return {0: 'Prefer encrypted', 1: 'Require encrypted', 2: 'Allow unencrypted'}.get(value, str(value))
    return str(value)


class ResultsTab(ctk.CTkScrollableFrame):
    def __init__(self, master, controller):
        super().__init__(master, fg_color='transparent')
        self.controller = controller
        card(self, 'Review changes before applying', 'A recommendation is a proposed setting, not a measured improvement.\nApplying saves the previous values, updates qBittorrent, checks that they took effect, and starts the second speed test.\nFor an official-image test, confirmation includes deleting its old ISO and downloading a fresh copy.')
        self.calc_btn = ctk.CTkButton(self, text='Refresh recommendations', command=controller.preview)
        self.calc_btn.pack(anchor='w', padx=16, pady=8)
        self.summary = ctk.CTkLabel(self, text='Confirm your connection, hardware and goals first.', wraplength=720, anchor='w', justify='left')
        self.summary.pack(fill='x', padx=16, pady=8)
        self.changes = ctk.CTkFrame(self, fg_color='transparent')
        self.changes.pack(fill='x')
        self.apply_btn = ctk.CTkButton(self, text='Save original settings, apply & test speed', command=controller.apply, state='disabled')
        self.apply_btn.pack(fill='x', padx=16, pady=16)

    def set_report(self, text):
        self.summary.configure(text=text)

    def show_plan(self, plan):
        for widget in self.changes.winfo_children():
            widget.destroy()
        measured = bool(self.controller.cycle.baseline)
        self.set_report(f"{len(plan['after'])} proposed changes • " + ('Starting speed saved. Ready to apply.' if measured else 'Preview only. Test your starting speed before applying.'))
        for key, value in plan['after'].items():
            title, detail = LABELS.get(key, (key.replace('_', ' ').title(), 'Advanced qBittorrent preference.'))
            frame = card(self.changes, title, detail)
            ctk.CTkLabel(frame, text=f"Current   {display(key, plan['before'][key])}     →     Proposed   {display(key, value)}", font=('Segoe UI', 16, 'bold'), text_color='#73c991').pack(anchor='w', padx=16, pady=(0, 12))
        if plan['warnings']:
            card(self.changes, 'Things to check', '\n'.join(plan['warnings']))
        details = ctk.CTkTextbox(self.changes, height=120, wrap='word')
        details.pack(fill='x', padx=16, pady=8)
        details.insert('1.0', 'Advanced details • settings left unchanged\n' + '\n'.join(plan['omitted']))
        details.configure(state='disabled')


class OutcomeTab(ctk.CTkScrollableFrame):
    def __init__(self, master, controller):
        super().__init__(master, fg_color='transparent')
        card(self, 'Your measured results', 'Compare actual transfer speeds from the same active torrents. Small differences can come from changing peers and network conditions.')
        self.summary = ctk.CTkLabel(self, text='Complete both speed tests to see a comparison.', wraplength=720, justify='left', anchor='w')
        self.summary.pack(fill='x', padx=16, pady=8)
        self.chart = tk.Canvas(self, height=240, background='#242a33', highlightthickness=0)
        self.chart.pack(fill='x', padx=16, pady=12)
        self.chart.bind('<Configure>', lambda _: self.draw())
        self.cycle = None
        self.ramp_card = card(self, 'How quickly the download gets going',
            'Before and after share the same time and speed scales. Time starts when the tuner requests a fresh test download.\n50% / 90% use the median speed of each run in its final 30 samples and require three consecutive readings.\nReadings are approximately one second apart. Peer counts mean connected users; they do not count every seed available online. A second run can reuse cached peer addresses.')
        self.ramp_summary = ctk.CTkLabel(self.ramp_card, text='A fresh test records startup, speed and connected peers.', justify='left', anchor='w', wraplength=720)
        self.ramp_summary.pack(fill='x', padx=16, pady=8)
        self.speed_curve = tk.Canvas(self.ramp_card, height=260, background='#242a33', highlightthickness=0)
        self.speed_curve.pack(fill='x', padx=16, pady=8)
        self.peer_curve = tk.Canvas(self.ramp_card, height=230, background='#242a33', highlightthickness=0)
        self.peer_curve.pack(fill='x', padx=16, pady=(0, 16))
        for canvas in (self.speed_curve, self.peer_curve):
            canvas.bind('<Configure>', lambda _: self.draw_ramp())
        self.saved = ctk.CTkLabel(self, text='No changes saved yet.', wraplength=720, justify='left', anchor='w')
        self.saved.pack(fill='x', padx=16, pady=12)
        self.rollback_btn = ctk.CTkButton(self, text='Undo this tuning • restore original settings', fg_color='#663e46', command=controller.rollback, state='disabled')
        self.rollback_btn.pack(fill='x', padx=16, pady=8)
        ctk.CTkButton(self, text='Recover settings from an earlier session…', fg_color='#404956', command=controller.restore_backup).pack(anchor='w', padx=16, pady=8)

    def show_cycle(self, cycle):
        self.cycle = cycle
        complete = bool(cycle.baseline and cycle.optimized)
        self.summary.configure(text=cycle.comparison() if complete else ('Changes checked in qBittorrent. The second speed test is still needed.' if cycle.original is not None else 'Starting-speed test saved. Review changes, then run the after test.'))
        active = cycle.original is not None
        self.saved.configure(text=('Original values saved before applying. Undo restores only the settings changed by this tuning; it keeps your torrents and downloaded files.\nBackup: ' + str(cycle.path)) if active else 'No tuning is currently applied. The experiment record is retained; torrents and downloaded files are kept.')
        self.draw()
        self.draw_ramp()

    def draw(self):
        self.chart.delete('all')
        cycle = self.cycle
        if not cycle or not cycle.baseline or not cycle.optimized:
            self.chart.create_text(20, 40, anchor='w', fill='#b6c4d6', text='Before / after chart appears when both tests finish.')
            return
        width = max(300, self.chart.winfo_width())
        for index, direction in enumerate(('download', 'upload')):
            before = cycle.baseline[f'mean_{direction}_mib_s']
            after = cycle.optimized[f'mean_{direction}_mib_s']
            maximum = max(before, after, .01)
            y = 20 + index * 110
            self.chart.create_text(16, y, anchor='w', fill='white', text=direction.title(), font=('Segoe UI', 13, 'bold'))
            for row, (name, value, color) in enumerate((('Before', before, '#8194ad'), ('After', after, '#48bfa1'))):
                top = y + 20 + row * 30
                self.chart.create_text(16, top + 10, anchor='w', fill='white', text=name)
                self.chart.create_rectangle(80, top, 80 + (width-240)*value/maximum, top+20, fill=color, outline='')
                self.chart.create_text(width-145, top+10, anchor='w', fill='white', text=f'{value:.2f} MiB/s')


    def draw_ramp(self):
        cycle = self.cycle
        runs = [(name, data, color) for name, data, color in (
            ('Before', getattr(cycle, 'baseline', None), '#8194ad'),
            ('After', getattr(cycle, 'optimized', None), '#48bfa1'))
            if data and data.get('ramp_trace')]
        def seconds(value):
            return 'not observed' if value is None else f'{value:.1f} s'
        lines = []
        for name, data, _ in runs:
            metrics = data.get('ramp_metrics', {})
            if metrics.get('fresh_start'):
                lines.append(f"{name} • First observed traffic: {seconds(metrics.get('first_traffic_seconds'))} • First observed seed: {seconds(metrics.get('first_seed_seconds'))}\n"
                             f"50%: {seconds(metrics.get('time_to_50_seconds'))} • 90%: {seconds(metrics.get('time_to_90_seconds'))} • "
                             f"Reference speed: {metrics.get('reference_download_mib_s', 0):.2f} MiB/s")
            else:
                lines.append(f'{name} • Already active: curves start at observation, so launch time is unavailable.')
        self.ramp_summary.configure(text='\n\n'.join(lines) if lines else 'Run a new test to record startup and connected peers. Older results have no startup history.')
        for canvas, title, keys, divisor in (
            (self.speed_curve, 'Download speed • MiB/s', ('download',), 1024**2),
            (self.peer_curve, 'Connected seeds (solid) • all connected peers (dashed)', ('seeds', 'peers'), 1)):
            canvas.delete('all')
            width, height = max(300, canvas.winfo_width()), int(canvas.cget('height'))
            left, right, top, bottom = 55, width-25, 50, height-40
            canvas.create_text(16, 18, anchor='w', fill='white', text=title)
            canvas.create_text(width-160, 18, anchor='w', fill='#8194ad', text='Before')
            canvas.create_text(width-85, 18, anchor='w', fill='#48bfa1', text='After')
            points = [p for _, data, _ in runs for p in data['ramp_trace']['samples']]
            if not points:
                canvas.create_text(16, 80, anchor='w', fill='#b6c4d6', text='No recorded curve yet.')
                continue
            xmax = max(1, max(p['elapsed'] for p in points))
            ymax = max(1, max((p[key]/divisor for p in points for key in keys if p.get(key) is not None), default=1))
            for tick in range(5):
                x = left+(right-left)*tick/4
                y = bottom-(bottom-top)*tick/4
                canvas.create_line(left, y, right, y, fill='#3c4654')
                canvas.create_text(left-8, y, anchor='e', fill='#b6c4d6', text=f'{ymax*tick/4:.1f}' if divisor > 1 else f'{ymax*tick/4:.0f}')
                canvas.create_text(x, bottom+14, fill='#b6c4d6', text=f'{xmax*tick/4:.0f}s')
            for _, data, color in runs:
                for key in keys:
                    coordinates = []
                    for point in data['ramp_trace']['samples']:
                        value = point.get(key)
                        if value is None:
                            continue
                        coordinates.extend((left+(right-left)*point['elapsed']/xmax,
                                            bottom-(bottom-top)*(value/divisor)/ymax))
                    if len(coordinates) >= 4:
                        canvas.create_line(*coordinates, fill=color, width=2,
                                           dash=(5, 3) if key == 'peers' else ())
                    elif coordinates:
                        x, y = coordinates
                        canvas.create_oval(x-2, y-2, x+2, y+2, fill=color, outline='')
            canvas.create_line(left, top, left, bottom, right, bottom, fill='#b6c4d6')

