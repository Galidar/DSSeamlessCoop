/*
 * Bonfire relay service.
 *
 * Hosts behind CGNAT keep one outbound control connection to this process.
 * Public TCP/UDP sockets on the relay are then multiplexed over that control
 * connection and forwarded by BonfireService back to the host's local Server.exe.
 */

const net = require('net');
const dgram = require('dgram');

class RelayManager {
    constructor(config) {
        this.config = Object.assign({
            enabled: false,
            control_port: 50030,
            public_hostname: '',
            min_port: 51000,
            max_port: 51999,
            shared_secret: ''
        }, config || {});

        this.allocations = new Map();
        this.usedPorts = new Set();
        this.nextPort = this.config.min_port;
        this.controlServer = null;
    }

    start() {
        if (!this.config.enabled) {
            console.log('Bonfire relay is disabled.');
            return;
        }

        if (!this.config.public_hostname) {
            console.warn('Bonfire relay enabled without relay.public_hostname; clients need a public IPv4 address.');
        }

        this.controlServer = net.createServer((socket) => this.handleControl(socket));
        this.controlServer.listen(this.config.control_port, () => {
            console.log(`Bonfire relay control is listening on port ${this.config.control_port}.`);
        });
    }

    handleControl(socket) {
        socket.setNoDelay(true);

        let buffer = '';
        let allocation = null;

        const cleanup = () => {
            if (allocation) {
                this.closeAllocation(allocation);
                allocation = null;
            }
        };

        socket.on('data', (chunk) => {
            buffer += chunk.toString('utf8');
            for (;;) {
                const index = buffer.indexOf('\n');
                if (index < 0) break;

                const line = buffer.slice(0, index).trim();
                buffer = buffer.slice(index + 1);
                if (!line) continue;

                let frame = null;
                try {
                    frame = JSON.parse(line);
                } catch (err) {
                    this.sendFrame(socket, { type: 'error', message: 'Malformed relay frame.' });
                    socket.destroy();
                    return;
                }

                if (frame.type === 'register') {
                    this.register(socket, frame)
                        .then((created) => {
                            cleanup();
                            allocation = created;
                        })
                        .catch((err) => {
                            this.sendFrame(socket, { type: 'error', message: err.message || String(err) });
                            socket.destroy();
                        });
                    continue;
                }

                if (!allocation) {
                    this.sendFrame(socket, { type: 'error', message: 'Relay is not registered.' });
                    socket.destroy();
                    return;
                }

                this.handleHostFrame(allocation, frame);
            }
        });

        socket.on('close', cleanup);
        socket.on('error', cleanup);
    }

    async register(socket, frame) {
        if (this.config.shared_secret && frame.token !== this.config.shared_secret) {
            throw new Error('Relay token rejected.');
        }

        const serverId = String(frame.serverId || '').trim();
        if (!serverId) {
            throw new Error('serverId is required.');
        }

        if (this.allocations.has(serverId)) {
            this.closeAllocation(this.allocations.get(serverId));
        }

        const ports = {
            login: this.reservePort(),
            auth: this.reservePort(),
            game: this.reservePort()
        };

        const allocation = {
            serverId,
            socket,
            ports,
            tcpServers: [],
            tcpChannels: new Map(),
            udpSocket: null,
            udpEndpoints: new Set(),
            nextChannel: 1
        };

        try {
            allocation.tcpServers.push(await this.listenTcp(allocation, 'login', ports.login));
            allocation.tcpServers.push(await this.listenTcp(allocation, 'auth', ports.auth));
            allocation.udpSocket = await this.listenUdp(allocation, ports.game);
        } catch (err) {
            this.closeAllocation(allocation);
            throw err;
        }

        this.allocations.set(serverId, allocation);

        this.sendFrame(socket, {
            type: 'registered',
            publicHost: this.config.public_hostname,
            loginPort: ports.login,
            authPort: ports.auth,
            gamePort: ports.game
        });

        console.log(`Relay registered server ${serverId}: login=${ports.login} auth=${ports.auth} game=${ports.game}`);
        return allocation;
    }

    listenTcp(allocation, service, port) {
        return new Promise((resolve, reject) => {
            const server = net.createServer((clientSocket) => {
                clientSocket.setNoDelay(true);
                const channel = allocation.nextChannel++;
                allocation.tcpChannels.set(channel, { service, socket: clientSocket });

                this.sendFrame(allocation.socket, { type: 'tcp_open', channel, service });

                clientSocket.on('data', (chunk) => {
                    this.sendFrame(allocation.socket, {
                        type: 'tcp_data',
                        channel,
                        data: chunk.toString('base64')
                    });
                });

                const close = () => {
                    if (allocation.tcpChannels.delete(channel)) {
                        this.sendFrame(allocation.socket, { type: 'tcp_close', channel });
                    }
                };
                clientSocket.on('close', close);
                clientSocket.on('error', close);
            });

            server.once('error', reject);
            server.listen(port, () => resolve(server));
        });
    }

    listenUdp(allocation, port) {
        return new Promise((resolve, reject) => {
            const socket = dgram.createSocket('udp4');
            socket.once('error', reject);
            socket.on('message', (msg, rinfo) => {
                const endpoint = `${rinfo.address}:${rinfo.port}`;
                allocation.udpEndpoints.add(endpoint);
                this.sendFrame(allocation.socket, {
                    type: 'udp_data',
                    endpoint,
                    data: msg.toString('base64')
                });
            });
            socket.bind(port, () => resolve(socket));
        });
    }

    handleHostFrame(allocation, frame) {
        if (frame.type === 'tcp_data') {
            const channel = allocation.tcpChannels.get(frame.channel);
            if (channel) {
                channel.socket.write(Buffer.from(frame.data || '', 'base64'));
            }
            return;
        }

        if (frame.type === 'tcp_close') {
            const channel = allocation.tcpChannels.get(frame.channel);
            if (channel) {
                allocation.tcpChannels.delete(frame.channel);
                channel.socket.destroy();
            }
            return;
        }

        if (frame.type === 'udp_data') {
            if (!allocation.udpSocket || !frame.endpoint) return;
            const endpoint = String(frame.endpoint);
            const split = endpoint.lastIndexOf(':');
            if (split <= 0) return;
            const address = endpoint.slice(0, split);
            const port = parseInt(endpoint.slice(split + 1), 10);
            if (!Number.isFinite(port)) return;

            allocation.udpSocket.send(Buffer.from(frame.data || '', 'base64'), port, address);
            return;
        }

        if (frame.type === 'ping') {
            this.sendFrame(allocation.socket, { type: 'pong' });
        }
    }

    reservePort() {
        const min = this.config.min_port;
        const max = this.config.max_port;
        const count = max - min + 1;

        for (let i = 0; i < count; i++) {
            const port = this.nextPort;
            this.nextPort++;
            if (this.nextPort > max) this.nextPort = min;

            if (!this.usedPorts.has(port)) {
                this.usedPorts.add(port);
                return port;
            }
        }

        throw new Error('Relay public port range is exhausted.');
    }

    releasePort(port) {
        if (port) this.usedPorts.delete(port);
    }

    closeAllocation(allocation) {
        if (!allocation) return;

        this.allocations.delete(allocation.serverId);

        for (const channel of allocation.tcpChannels.values()) {
            try { channel.socket.destroy(); } catch (_) {}
        }
        allocation.tcpChannels.clear();

        for (const server of allocation.tcpServers) {
            try { server.close(); } catch (_) {}
        }

        if (allocation.udpSocket) {
            try { allocation.udpSocket.close(); } catch (_) {}
        }

        this.releasePort(allocation.ports.login);
        this.releasePort(allocation.ports.auth);
        this.releasePort(allocation.ports.game);

        console.log(`Relay unregistered server ${allocation.serverId}.`);
    }

    sendFrame(socket, frame) {
        if (!socket || socket.destroyed) return;
        socket.write(JSON.stringify(frame) + '\n');
    }
}

module.exports = RelayManager;
