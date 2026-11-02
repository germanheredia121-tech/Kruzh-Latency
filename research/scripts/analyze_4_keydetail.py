import json
import sys
sys.stdout.reconfigure(encoding='utf-8')

with open('blurbusters_inputlag.json', encoding='utf-8') as f:
    data = json.load(f)

# Threads de interes por ID - los mas importantes identificados
KEY_THREADS = {
    '15707': 'Floaty aim ultimate fix',
    '15703': 'HOW TO GET TRUE FULLSCREEN PERFORMANCE IN 2026',
    '15717': 'dont disable game bar',
    '15643': 'The desync gets worse or better depending on the BIOS version',
    '15352': 'Fixed my floaty mouse (Realtek onboard audio driver)',
    '15637': 'External SSD/HDD has fixed my desync and input lag',
    '15628': 'SPI flash / SMI/SMM + some observations (AM5)',
    '15634': 'Xhci may be the culprit',
    '15649': 'Easy Setting. Please Test and Report',
    '15638': 'Potential fix for desync, input lag, floaty mouse',
    '15657': 'Valorant Hitreg Desync Fixed',
    '15663': 'Fix for Desync, Floaty Mouse, Input Lag only for AM5',
    '15654': 'X3D processor tip',
    '14097': 'DSC causing input lag/heavy mouse feeling',
    '14494': 'Longstanding Floaty Aim Issue Fixed with Fiber Optic DP Cable',
    '15564': 'InputLag SOLVED Realtek USB / TP-LINK UE300 USB 3.0 Adapter',
    '15674': 'AMD KVM Mouse Protocol (Scewin)',
    '15658': 'Sharing 4 years of my experience',
    '15683': 'Tuning CS2 for Minimum Input Lag Consistency (Dual-CCD + 480Hz)',
    '13863': 'USB Headsets causing different feeling depending on usb port',
    '10508': '[PSA] Audio enhancements cause big interrupts to process latency',
    '15665': 'BCDEdit Timer Settings on Windows 11 / Ryzen X3D',
    '12413': 'Fixed floaty mouse by uninstalling Realtek Audio Driver',
    '15689': 'Floaty mouse discussion',
    '15673': 'My experience with the lag and temporary fixes',
    '15406': 'eSports monitors and their secrets',
    '15646': 'What NVIDIA Reflex Does - Detailed Information',
    '15113': 'The mouse drifting issue potential breakthrough',
    '11081': 'Ryzen vs Intel Input Lag/Latency',
}

with open('key_threads_detail.txt', 'w', encoding='utf-8') as out:
    for thread in data:
        tid = thread['thread_id']
        if tid in KEY_THREADS:
            out.write(f"\n{'='*80}\n")
            out.write(f"THREAD [{tid}]: {thread['title']}\n")
            out.write(f"URL: {thread['url']}\n")
            out.write(f"{'='*80}\n")
            for i, post in enumerate(thread['posts']):
                out.write(f"\n--- POST {i+1} by {post.get('author', '?')} ---\n")
                out.write(post.get('content', '')[:800] + "\n")

print("Key threads guardados en key_threads_detail.txt")
