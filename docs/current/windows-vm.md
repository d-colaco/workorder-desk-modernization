# The Windows VM (legacy desktop client)

The WPF client needs Windows, so it runs in a libvirt VM on the same host: **Windows Server 2012 R2** (out of support since October 2023, which is
what a legacy desktop estate looks like), 1 vCPU, 4 GB, the libvirt `default` NAT network (`192.168.122.0/24`; the host is `192.168.122.1` on
`virbr0`). The lab is built and measured without it (`make bench` stands in for the client), so this is optional; it is what lets the article say
the real client ran. `make wpf-deploy` and `make wpf-check` need everything below.

## Why it takes more than installing Windows

A bare 2012 R2 image has no updates, and that breaks a modern toolchain in a chain:

| Symptom | Cause | What fixed it |
|---|---|---|
| `ipconfig` shows no adapter | The VM's NIC model was `virtio`, which 2012 R2 has no in-box driver for | Change the NIC model to `e1000e` (virt-manager → NIC → Device model), or install the virtio-win driver |
| Browsers and `Invoke-WebRequest` fail with "Could not create SSL/TLS secure channel" | No TLS 1.2 defaults in .NET and no current root certificates | Install the updates below. Setting `[Net.ServicePointManager]::SecurityProtocol = Tls12` alone was not enough |
| Windows Update hangs on "checking for updates" | The update agent of an unpatched image | Same updates, installed offline |

**Getting files into the VM without TLS:** serve a folder from the host on the bridge (`cd ~/vmfiles && python3 -m http.server 8000 --bind 192.168.122.1`)
and fetch with `(New-Object Net.WebClient).DownloadFile("http://192.168.122.1:8000/<file>", "$env:TEMP\<file>")`. The host firewall needs
`sudo ufw allow in on virbr0 to any port 8000 proto tcp` (remove it afterwards). The same folder carries the installers below.

**Updates, offline, from the Microsoft Update Catalog** (search the update's Knowledge Base number without filtering on "Server"; several are listed as Windows 8.1), in this
order, rebooting between steps: KB2919442, then (run `clearcompressionflag.exe` first) KB2919355, then KB2932046, KB2959977, KB2937592, KB2938439
and KB2934018, then KB3173424 and KB3172614. Install each with `wusa <file>.msu /quiet /norestart`. After that, TLS downloads work, and
**.NET Framework 4.8** (the offline installer) installs; the net48 client needs it.

## What the lab needs from the VM

1. **A fixed lease**, so the host can name it: `sudo virsh net-update default add ip-dhcp-host "<host mac='<the VM's MAC>' name='win2k12r2' ip='192.168.122.50'/>" --live --config`.
2. **OpenSSH server** (the `Win32-OpenSSH` MSI from `PowerShell/Win32-OpenSSH`), service `sshd` automatic, and a firewall rule that allows TCP 22 **from `192.168.122.1` only**.
3. **A local user `lab`** with your public key in `C:\Users\lab\.ssh\authorized_keys` (or, if `lab` is an administrator, in
   `C:\ProgramData\ssh\administrators_authorized_keys`), and a host alias in `~/.ssh/config`:
   `Host dclab-vm` / `HostName 192.168.122.50` / `User lab` / `IdentityFile ~/.ssh/dclab_vm`. Check with `ssh dclab-vm hostname`.
   Without working key login, `make wpf-deploy` still works but asks for the password for each connection.
4. **Host firewall**: the VM must reach SQL Server and the telemetry collector on the bridge:
   `sudo ufw allow in on virbr0 to any port 32131 proto tcp` and `... port 32135 ...`. The compose file binds those two ports to `192.168.122.1` as well as loopback.
5. **Check and deploy**: `make wpf-check` (the VM opens TCP to 32131 and 32135), then `make wpf-build && make wpf-deploy`, which copies the client to
   `C:\Lab\wpf\` with the `lab_app` connection string written from `.env`.

## Running the client

Start `C:\Lab\wpf\WorkOrderDesk.exe` **from the VM's desktop** (the console in virt-manager or RDP). A GUI started over SSH runs in a hidden session.
The services are `workorder-desk-wpf-client` in Grafana's traces. Windows' default locale is not forced: dates typed into the routing fields parse in the VM's
culture, so ISO dates (`2026-10-17 11:05`) are the safe choice, and decimals use a dot.

## Known limits

- The VM's clock was about **0.6 s behind the host** in the run recorded in [verification.md](verification.md); client traces therefore show a gap
  before the relay's wait span, and cross-machine timings of client traces are not quoted.
- After the VM was resumed from a stopped state its clock was **3 h 14 min behind** (client spans drawn hours before the rest of the trace). Check `Get-Date`
  against the host before taking client traces, and fix with `Set-Date`; after the fix the offset was again about 0.7 s.
- The VM is out of support and was patched only with the updates listed above (and whatever Windows Update added afterwards); it is on a NAT network with no inbound route from outside the host. It is a lab machine.
