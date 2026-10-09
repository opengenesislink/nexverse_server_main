// SPDX-License-Identifier: MPL-2.0
package main

import (
	"context"
	"errors"
	"log/slog"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"
)

func main() {
	cfg, err := loadConfig()
	if err != nil {
		slog.Error("OGLVoice configuration rejected", "error", err)
		os.Exit(1)
	}
	server := newMediaServer(cfg)
	httpServer := &http.Server{
		Addr:              cfg.Bind,
		Handler:           server.routes(),
		ReadHeaderTimeout: 5 * time.Second,
		ReadTimeout:       20 * time.Second,
		WriteTimeout:      30 * time.Second,
		IdleTimeout:       30 * time.Second,
		MaxHeaderBytes:    8192,
	}
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	go func() {
		ticker := time.NewTicker(30 * time.Second)
		defer ticker.Stop()
		for {
			select {
			case <-ctx.Done():
				return
			case <-ticker.C:
				server.reapOlderThan(10 * time.Minute)
			}
		}
	}()

	slog.Info("OGLVoice media bridge listening", "bind", cfg.Bind,
		"node_count", len(cfg.Nodes), "tenant_count", len(cfg.Tenants))
	errCh := make(chan error, 1)
	go func() { errCh <- httpServer.ListenAndServe() }()
	select {
	case <-ctx.Done():
	case err = <-errCh:
		if err != nil && !errors.Is(err, http.ErrServerClosed) {
			slog.Error("OGLVoice media HTTP failure", "error", err)
			os.Exit(1)
		}
	}
	shutdown, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	_ = httpServer.Shutdown(shutdown)
	server.mu.Lock()
	peers := make([]*peerState, 0, len(server.sessions))
	for _, p := range server.sessions { peers = append(peers, p) }
	server.mu.Unlock()
	for _, p := range peers { p.close() }
}
