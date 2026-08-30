window.ThroughputChart = {
    _chart: null,

    init: function (canvasId) {
        const ctx = document.getElementById(canvasId);
        if (!ctx) return;

        this._chart = new Chart(ctx, {
            type: 'line',
            data: {
                labels: [],
                datasets: [
                    {
                        label: 'Succeeded',
                        data: [],
                        borderColor: '#065f46',
                        backgroundColor: 'rgba(6, 95, 70, 0.1)',
                        fill: true,
                        tension: 0.3
                    },
                    {
                        label: 'Failed',
                        data: [],
                        borderColor: '#991b1b',
                        backgroundColor: 'rgba(153, 27, 27, 0.1)',
                        fill: true,
                        tension: 0.3
                    },
                    {
                        label: 'Dead',
                        data: [],
                        borderColor: '#1f2937',
                        backgroundColor: 'rgba(31, 41, 55, 0.1)',
                        fill: true,
                        tension: 0.3
                    }
                ]
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                animation: { duration: 300 },
                scales: {
                    x: {
                        ticks: { maxTicksLimit: 10, font: { size: 11 } }
                    },
                    y: {
                        beginAtZero: true,
                        ticks: {
                            stepSize: 1,
                            font: { size: 11 }
                        },
                        title: { display: true, text: 'Jobs / 5s' }
                    }
                },
                plugins: {
                    legend: { position: 'top' },
                    tooltip: { mode: 'index', intersect: false }
                }
            }
        });
    },

    update: function (labels, succeeded, failed, dead) {
        if (!this._chart) return;
        this._chart.data.labels = labels;
        this._chart.data.datasets[0].data = succeeded;
        this._chart.data.datasets[1].data = failed;
        this._chart.data.datasets[2].data = dead;
        this._chart.update('none'); // 'none' skips animation for live updates
    },

    destroy: function () {
        if (this._chart) {
            this._chart.destroy();
            this._chart = null;
        }
    }
};