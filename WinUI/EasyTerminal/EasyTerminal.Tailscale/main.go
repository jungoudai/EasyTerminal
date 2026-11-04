// easyterminal-tailscale runs an embedded Tailscale node (tsnet) for EasyTerminal.
//
// The node lives entirely in this process: no Tailscale service, no TUN adapter and no
// administrator rights. EasyTerminal reaches the tailnet through the SOCKS5 proxy that tsnet
// serves on a loopback port, and manages the node (login, status, logout) through the
// LocalAPI served on that same port.
//
// Protocol with the parent process:
//   - On startup one JSON line is written to stdout:
//     {"addr":"127.0.0.1:port","proxyUser":"tsnet","proxyPassword":"…","localApiPassword":"…"}
//     or {"error":"…"} when the node could not start.
//   - The node shuts down when stdin closes, so it never outlives the app.
//   - Diagnostics go to stderr.
package main

import (
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"log"
	"os"
	"strings"

	"tailscale.com/tsnet"
)

type ready struct {
	Addr             string `json:"addr,omitempty"`
	ProxyUser        string `json:"proxyUser,omitempty"`
	ProxyPassword    string `json:"proxyPassword,omitempty"`
	LocalAPIPassword string `json:"localApiPassword,omitempty"`
	Error            string `json:"error,omitempty"`
}

func emit(r ready) {
	line, _ := json.Marshal(r)
	os.Stdout.Write(append(line, '\n'))
}

func main() {
	dir := flag.String("dir", "", "directory holding the node's state")
	hostname := flag.String("hostname", "easyterminal", "name of this node in the tailnet")
	controlURL := flag.String("control-url", "", "coordination server; empty means Tailscale's own (set for Headscale)")
	ephemeral := flag.Bool("ephemeral", false, "remove the node from the tailnet when it goes offline")
	verbose := flag.Bool("verbose", false, "write tsnet's backend log to stderr")
	flag.Parse()

	if *dir == "" {
		emit(ready{Error: "missing -dir"})
		os.Exit(2)
	}
	if err := os.MkdirAll(*dir, 0o700); err != nil {
		emit(ready{Error: err.Error()})
		os.Exit(1)
	}

	userLog := log.New(os.Stderr, "", log.LstdFlags)
	srv := &tsnet.Server{
		Dir:        *dir,
		Hostname:   *hostname,
		ControlURL: strings.TrimSpace(*controlURL),
		Ephemeral:  *ephemeral,
		// Read from the environment, never the command line, so it does not show in process lists.
		AuthKey:  os.Getenv("EASYTERMINAL_TS_AUTHKEY"),
		UserLogf: userLog.Printf,
		Logf:     func(string, ...any) {},
	}
	if *verbose {
		srv.Logf = userLog.Printf
	}
	os.Unsetenv("EASYTERMINAL_TS_AUTHKEY")

	if err := srv.Start(); err != nil {
		emit(ready{Error: fmt.Sprintf("start: %v", err)})
		os.Exit(1)
	}
	addr, proxyCred, localAPICred, err := srv.Loopback()
	if err != nil {
		emit(ready{Error: fmt.Sprintf("loopback: %v", err)})
		srv.Close()
		os.Exit(1)
	}
	emit(ready{Addr: addr, ProxyUser: "tsnet", ProxyPassword: proxyCred, LocalAPIPassword: localAPICred})

	// The parent holds our stdin open for as long as it wants the node.
	io.Copy(io.Discard, os.Stdin)
	srv.Close()
}
